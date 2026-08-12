using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class NaverLandClient : IDisposable
{
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(30);
    private readonly HttpClient _httpClient;
    private readonly ApiConfiguration _apiConfiguration;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public NaverLandClient(HttpMessageHandler? handler = null)
        : this(new ApiConfiguration(), handler)
    {
    }

    public NaverLandClient(ApiConfiguration apiConfiguration, HttpMessageHandler? handler = null)
    {
        _apiConfiguration = apiConfiguration;
        handler ??= new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<Listing>> GetOwnListingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var listings = new Dictionary<string, Listing>();
        var manualArticleNumbers = ParseManualArticleNumbers(settings.ManualArticleNumbers);

        // 매물번호가 입력되면 단체 전체 목록보다 우선한다. 불필요한 전체 목록 호출을
        // 피하면서 특정 매물만 빠르게 확인할 수 있다.
        if (manualArticleNumbers.Count > 0)
        {
            foreach (var articleNo in manualArticleNumbers)
            {
                listings[articleNo] = new Listing(
                    articleNo, "매물번호 직접 조회", string.Empty, string.Empty,
                    string.Empty, settings.GroupId, string.Empty, string.Empty, string.Empty, string.Empty, true);
            }
            return listings.Values.ToList();
        }

        if (!string.IsNullOrWhiteSpace(settings.GroupId))
        {
            for (var page = 1; page <= 1000; page++)
            {
                var path = BuildArticleListPath(settings.GroupId.Trim(), page);
                var json = await GetStringAsync(path, _apiConfiguration.RealtorArticleList, settings, cancellationToken);
                var parsed = NaverResponseParser.ParseArticleResponse(json);
                foreach (var listing in parsed.Listings) listings[listing.ArticleNo] = listing with { IsMine = true };
                if (parsed.Listings.Count == 0 || parsed.IsMoreData == false) break;
                // 실제 HTTP 호출 간격은 GetStringAsync의 전역 요청 게이트에서 보장한다.
            }
        }

        return listings.Values.ToList();
    }

    public async Task<RankingResult> GetRankingAsync(
        Listing ownListing,
        ISet<string> ownArticleNumbers,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = BuildPath(_apiConfiguration.Ranking, new Dictionary<string, string>
            {
                ["representativeArticleNo"] = ownListing.ArticleNo
            });
            var json = await GetStringAsync(path, _apiConfiguration.Ranking, settings, cancellationToken);
            var parsed = NaverResponseParser.ParseArticleResponse(json, ownArticleNumbers);
            var prices = NaverResponseParser.ParseSameAddressPrices(json);
            var rank = parsed.Listings
                .Select((listing, index) => new { listing.ArticleNo, Rank = index + 1 })
                .FirstOrDefault(x => x.ArticleNo == ownListing.ArticleNo)?.Rank;
            var hydratedOwn = parsed.Listings.FirstOrDefault(x => x.ArticleNo == ownListing.ArticleNo);

            return new RankingResult(
                hydratedOwn is null ? ownListing : hydratedOwn with { IsMine = true },
                rank,
                parsed.Listings.Count,
                prices.MinPrice,
                prices.MaxPrice,
                parsed.Listings);
        }
        catch (NaverApiException ex)
        {
            return new RankingResult(ownListing, null, 0, null, null, [], ex.Message);
        }
        catch (Exception ex)
        {
            return new RankingResult(ownListing, null, 0, null, null, [], $"응답 처리 실패: {ex.Message}");
        }
    }

    private async Task<string> GetStringAsync(
        string path,
        ApiEndpointConfiguration endpointConfiguration,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            throw CreateCooldownException(blockedUntil, settings.RateLimitCooldownSource);
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (settings.RateLimitBlockedUntilUtc is { } gateBlockedUntil && gateBlockedUntil > DateTime.UtcNow)
                throw CreateCooldownException(gateBlockedUntil, settings.RateLimitCooldownSource);

            var remainingDelay = MinimumRequestInterval - (DateTime.UtcNow - _lastRequestUtc);
            if (remainingDelay > TimeSpan.Zero) await Task.Delay(remainingDelay, cancellationToken);

        var baseUrl = _apiConfiguration.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        ApplyConfiguredHeaders(request, endpointConfiguration);

        HttpResponseMessage response;
        try
        {
            _lastRequestUtc = DateTime.UtcNow;
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NaverApiException("네이버 응답 시간이 15초를 초과했습니다.");
        }
        catch (HttpRequestException ex)
        {
            throw new NaverApiException($"네트워크 오류: {ex.Message}", null, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                settings.RateLimitBlockedUntilUtc = null;
                settings.RateLimitCooldownSource = string.Empty;
                return body;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var cooldown = GetRetryAt(response);
                settings.RateLimitBlockedUntilUtc = cooldown.RetryAtUtc;
                settings.RateLimitCooldownSource = cooldown.Source;
                throw CreateCooldownException(cooldown.RetryAtUtc, cooldown.Source);
            }

            var message = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => "접근이 거부되었습니다(403). 쿠키와 Bearer 토큰을 갱신하세요.",
                HttpStatusCode.Unauthorized => "인증이 만료되었습니다(401). 쿠키와 Bearer 토큰을 갱신하세요.",
                _ => $"네이버 API 오류: HTTP {(int)response.StatusCode}"
            };
            throw new NaverApiException(message, response.StatusCode);
        }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private string BuildArticleListPath(string userId, int page)
    {
        var profile = _apiConfiguration.RealtorArticleList;
        return BuildPath(profile, new Dictionary<string, string>
        {
            [profile.RealtorIdParameter] = userId,
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private static string BuildPath(
        ApiEndpointConfiguration profile,
        IReadOnlyDictionary<string, string> overrides)
    {
        var parameters = new Dictionary<string, string>(profile.Params, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides) parameters[pair.Key] = pair.Value;
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return $"{profile.Endpoint}?{query}";
    }

    private void ApplyConfiguredHeaders(HttpRequestMessage request, ApiEndpointConfiguration profile)
    {
        foreach (var header in profile.Headers)
        {
            var value = header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                ? NormalizeCookieHeader(header.Value)
                : header.Value;
            request.Headers.TryAddWithoutValidation(header.Key, value);
        }

        if (request.Headers.Accept.Count == 0)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (request.Headers.AcceptLanguage.Count == 0)
            request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9");
        if (request.Headers.Referrer is null)
            request.Headers.Referrer = new Uri(_apiConfiguration.BaseUrl.TrimEnd('/') + "/");
        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
    }

    private static (DateTime RetryAtUtc, string Source) GetRetryAt(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Date is { } date)
            return (date.UtcDateTime, "네이버 Retry-After 응답 기준");
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return (DateTime.UtcNow + delta, "네이버 Retry-After 응답 기준");
        return (DateTime.UtcNow + DefaultRateLimitCooldown, "Retry-After가 없어 앱 기본 30분 적용");
    }

    private static NaverApiException CreateCooldownException(DateTime blockedUntilUtc, string? source)
    {
        var localTime = blockedUntilUtc.ToLocalTime();
        var reason = string.IsNullOrWhiteSpace(source) ? "이전 버전에서 저장된 429 보호 대기(기본 최대 30분)" : source;
        return new NaverApiException(
            $"호출 제한(429): {reason}. {localTime:HH:mm} 이후 다시 시도하세요.",
            HttpStatusCode.TooManyRequests);
    }

    public static IReadOnlyList<string> ParseManualArticleNumbers(string value)
    {
        var text = value ?? string.Empty;
        var queryArticleNumbers = Regex.Matches(
                text,
                @"(?:[?&]|\b)articleNo=(?<number>\d{6,})",
                RegexOptions.IgnoreCase)
            .Select(match => match.Groups["number"].Value)
            .Distinct()
            .ToList();
        if (queryArticleNumbers.Count > 0) return queryArticleNumbers;

        return Regex.Matches(text, @"\d{6,}")
            .Select(match => match.Value)
            .Distinct()
            .ToList();
    }

    public static string NormalizeCookieHeader(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw.Trim();
        if (!text.Contains('{')) return text.Replace("\r", "").Replace("\n", "; ").Trim(' ', ';');

        var pairs = Regex.Matches(text, @"['""](?<key>[^'""]+)['""]\s*:\s*['""](?<value>[^'""]*)['""]")
            .Select(match => $"{match.Groups["key"].Value}={match.Groups["value"].Value}")
            .ToList();
        return string.Join("; ", pairs);
    }

    public void Dispose()
    {
        _requestGate.Dispose();
        _httpClient.Dispose();
    }
}
