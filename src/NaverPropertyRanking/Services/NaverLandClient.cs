using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class NaverLandClient : IDisposable
{
    private static readonly TimeSpan ListingRequestInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RankingRequestInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(30);
    private readonly HttpClient _httpClient;
    private readonly ApiConfiguration _apiConfiguration;
    private readonly RequestThrottle _listingThrottle = new();
    private readonly RequestThrottle _rankingThrottle = new();
    private readonly RequestThrottle _articleDetailThrottle = new();
    private readonly RequestThrottle _complexDetailThrottle = new();
    private readonly RequestThrottle _advertisingThrottle = new();
    private readonly SemaphoreSlim _rankingConcurrency = new(5, 5);

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
        CancellationToken cancellationToken,
        IProgress<ListingLoadProgress>? progress = null)
    {
        var listings = new List<Listing>();
        await foreach (var listing in StreamOwnListingsAsync(settings, cancellationToken, progress)
                           .ConfigureAwait(false))
            listings.Add(listing);
        return listings;
    }

    public async IAsyncEnumerable<Listing> StreamOwnListingsAsync(
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IProgress<ListingLoadProgress>? progress = null)
    {
        var seenArticleNumbers = new HashSet<string>(StringComparer.Ordinal);
        var manualArticleNumbers = ParseManualArticleNumbers(settings.ManualArticleNumbers);

        // 매물번호가 입력되면 단체 전체 목록보다 우선한다. 불필요한 전체 목록 호출을
        // 피하면서 특정 매물만 빠르게 확인할 수 있다.
        if (manualArticleNumbers.Count > 0)
        {
            foreach (var articleNo in manualArticleNumbers)
            {
                yield return new Listing(
                    articleNo, "매물번호 직접 조회", string.Empty, string.Empty,
                    string.Empty, settings.GroupId, string.Empty, string.Empty, string.Empty, string.Empty, true);
            }
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(settings.GroupId))
        {
            const int maximumListingPages = 100;
            for (var page = 1; page <= maximumListingPages; page++)
            {
                var path = BuildArticleListPath(settings.GroupId.Trim(), page);
                var json = await GetStringAsync(
                        path,
                        _apiConfiguration.RealtorArticleList,
                        settings,
                        _listingThrottle,
                        ListingRequestInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                var parsed = NaverResponseParser.ParseArticleResponse(json);
                var addedCount = 0;
                foreach (var listing in parsed.Listings)
                {
                    if (!seenArticleNumbers.Add(listing.ArticleNo)) continue;
                    addedCount++;
                    yield return listing with { IsMine = true };
                }
                progress?.Report(new ListingLoadProgress(page, seenArticleNumbers.Count));
                if (parsed.Listings.Count == 0 || parsed.IsMoreData == false || addedCount == 0) break;
            }
        }
    }

    public async Task<RankingResult> GetRankingAsync(
        Listing ownListing,
        ISet<string> ownArticleNumbers,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        RankingResult result;
        await _rankingConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await GetRankingCoreAsync(
                    ownListing,
                    ownArticleNumbers,
                    settings,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _rankingConcurrency.Release();
        }

        // 랭킹 응답에도 단지번호가 없으면 이 매물을 처리하는 김에 상세 API로 바로 채운다.
        // 랭킹 게이트를 놓은 뒤에 호출하므로 랭킹 동시 실행 수를 잡아먹지 않고,
        // 상세 API는 별도 스로틀을 쓰기 때문에 랭킹 대기 시간에 그대로 묻힌다.
        return await BindComplexNoAsync(result, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 랭킹 결과의 내 매물에 단지번호가 비어 있으면 매물 상세 API로 보강한다.
    /// 상세 조회가 실패해도 랭킹 결과는 그대로 돌려준다.
    /// </summary>
    private async Task<RankingResult> BindComplexNoAsync(
        RankingResult result,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var ownListing = result.OwnListing;
        if (!string.IsNullOrWhiteSpace(ownListing.ComplexNo) ||
            string.IsNullOrWhiteSpace(ownListing.ArticleNo))
            return result;

        // 429 쿨다운 중에는 어차피 호출이 막히므로 상세 조회를 시도하지 않는다.
        if (settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
            return result;

        try
        {
            var detail = await GetArticleComparisonDetailAsync(ownListing, settings, cancellationToken)
                .ConfigureAwait(false);
            var complexNo = detail.Listing.ComplexNo;
            return string.IsNullOrWhiteSpace(complexNo)
                ? result
                : result with { OwnListing = ownListing with { ComplexNo = complexNo.Trim() } };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return result;
        }
    }

    public async Task<IReadOnlyList<AdvertisedRealtor>> GetComplexAdvertisingRealtorsAsync(
        string complexNo,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(complexNo)) return [];
        var normalizedComplexNo = complexNo.Trim();
        var profile = _apiConfiguration.ComplexAdvertising;
        var headerProfile = profile.Headers.Count > 0 ? profile : _apiConfiguration.Ranking;
        var endpoint = profile.Endpoint.Replace(
            "{complexNo}",
            Uri.EscapeDataString(normalizedComplexNo),
            StringComparison.OrdinalIgnoreCase);
        var listings = new List<Listing>();

        const int maximumPages = 10;
        for (var page = 1; page <= maximumPages; page++)
        {
            var path = BuildPath(
                profile,
                new Dictionary<string, string>
                {
                    ["complexNo"] = normalizedComplexNo,
                    ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                endpoint);
            var json = await GetStringAsync(
                    path,
                    headerProfile,
                    settings,
                    _advertisingThrottle,
                    ListingRequestInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = NaverResponseParser.ParseArticleResponse(json);
            listings.AddRange(parsed.Listings);
            var selected = AdvertisementAnalysisService.SelectTopRealtors(listings);
            if (selected.Count >= 3 || parsed.Listings.Count == 0 || parsed.IsMoreData == false)
                return selected;
        }

        return AdvertisementAnalysisService.SelectTopRealtors(listings);
    }

    /// <summary>
    /// 단지 광고 API(front-api/v1/realtor/advertisement)를 호출해
    /// 광고 상위 매물의 중개인 정보를 순위순으로 최대 3명 조회한다. 실패 시 빈 목록을 반환한다.
    /// </summary>
    public async Task<IReadOnlyList<ComplexAdvertisementRealtor>> GetComplexAdvertisementRealtorsAsync(
        string complexNo,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(complexNo)) return [];

        var normalizedComplexNo = complexNo.Trim();
        var profile = _apiConfiguration.RealtorAdvertisement;
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) return [];
        var headerProfile = profile.Headers.Count > 0
            ? profile
            : _apiConfiguration.RealtorArticleList.Headers.Count > 0
                ? _apiConfiguration.RealtorArticleList
                : _apiConfiguration.Ranking;
        var endpoint = profile.Endpoint.Replace(
            "{complexNo}",
            Uri.EscapeDataString(normalizedComplexNo),
            StringComparison.OrdinalIgnoreCase);
        var path = BuildPath(
            profile,
            new Dictionary<string, string> { ["complexNumber"] = normalizedComplexNo },
            endpoint);
        try
        {
            var json = await GetStringAsync(
                    path,
                    headerProfile,
                    settings,
                    _advertisingThrottle,
                    ListingRequestInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            return NaverResponseParser.ParseAdvertisementRealtors(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 단지 정보 API(/api/complexes/{complexNo})를 호출해 세대수·층수·사용승인일 등
    /// 단지 기본 정보를 조회한다. 실패 시 Error가 채워진 결과를 반환한다.
    /// </summary>
    public async Task<ComplexInformation> GetComplexInformationAsync(
        string complexNo,
        string fallbackName,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(complexNo))
            return new ComplexInformation(string.Empty, fallbackName) { Error = "단지번호 없음" };

        var normalizedComplexNo = complexNo.Trim();
        var profile = _apiConfiguration.ComplexDetail;
        var headerProfile = profile.Headers.Count > 0
            ? profile
            : _apiConfiguration.RealtorArticleList.Headers.Count > 0
                ? _apiConfiguration.RealtorArticleList
                : _apiConfiguration.Ranking;
        var endpoint = profile.Endpoint.Replace(
            "{complexNo}",
            Uri.EscapeDataString(normalizedComplexNo),
            StringComparison.OrdinalIgnoreCase);
        var path = profile.Params.Count > 0
            ? BuildPath(profile, new Dictionary<string, string>(), endpoint)
            : endpoint;
        try
        {
            var json = await GetStringAsync(
                    path,
                    headerProfile,
                    settings,
                    _complexDetailThrottle,
                    ListingRequestInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            return NaverResponseParser.ParseComplexInformation(json, normalizedComplexNo, fallbackName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ComplexInformation(normalizedComplexNo, fallbackName) { Error = ex.Message };
        }
    }

    public async Task<Listing> HydrateComplexIdentityAsync(
        Listing listing,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(listing.ComplexNo) || string.IsNullOrWhiteSpace(listing.ArticleNo))
            return listing;

        var detail = await GetArticleComparisonDetailAsync(listing, settings, cancellationToken)
            .ConfigureAwait(false);
        return detail.Listing;
    }

    public async Task<ArticleComparisonDetail> GetArticleComparisonDetailAsync(
        Listing listing,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(listing.ArticleNo))
            return new ArticleComparisonDetail(listing) { Error = "매물번호 없음" };

        var profile = _apiConfiguration.ArticleDetail;
        var headerProfile = profile.Headers.Count > 0
            ? profile
            : _apiConfiguration.RealtorArticleList.Headers.Count > 0
                ? _apiConfiguration.RealtorArticleList
                : _apiConfiguration.Ranking;
        var endpoint = profile.Endpoint.Replace(
            "{articleNo}",
            Uri.EscapeDataString(listing.ArticleNo.Trim()),
            StringComparison.OrdinalIgnoreCase);
        var json = await GetStringAsync(
                endpoint,
                headerProfile,
                settings,
                _articleDetailThrottle,
                ListingRequestInterval,
                cancellationToken)
            .ConfigureAwait(false);
        return NaverResponseParser.ParseArticleComparisonDetail(json, listing);
    }

    private async Task<RankingResult> GetRankingCoreAsync(
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
            var json = await GetStringAsync(
                    path,
                    _apiConfiguration.Ranking,
                    settings,
                    _rankingThrottle,
                    RankingRequestInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = NaverResponseParser.ParseArticleResponse(json, ownArticleNumbers);
            var prices = NaverResponseParser.ParseSameAddressPrices(json);
            var rank = parsed.Listings
                .Select((listing, index) => new { listing.ArticleNo, Rank = index + 1 })
                .FirstOrDefault(x => x.ArticleNo == ownListing.ArticleNo)?.Rank;
            var hydratedOwn = parsed.Listings.FirstOrDefault(x => x.ArticleNo == ownListing.ArticleNo);
            var resultOwnListing = hydratedOwn is null
                ? ownListing
                : hydratedOwn with
                {
                    IsMine = true,
                    ComplexNo = string.IsNullOrWhiteSpace(hydratedOwn.ComplexNo)
                        ? ownListing.ComplexNo
                        : hydratedOwn.ComplexNo,
                    ArticleName = string.IsNullOrWhiteSpace(hydratedOwn.ArticleName)
                        ? ownListing.ArticleName
                        : hydratedOwn.ArticleName
                };

            return new RankingResult(
                resultOwnListing,
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
        RequestThrottle throttle,
        TimeSpan minimumRequestInterval,
        CancellationToken cancellationToken)
    {
        if (settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            throw CreateCooldownException(blockedUntil, settings.RateLimitCooldownSource);
        }

        // 호출 시작 간격만 직렬 제어하고 응답 대기 중에는 다음 요청이 시작될 수 있게 한다.
        // 랭킹 배치의 최대 동시 요청 수는 호출 측에서 제한한다.
        await throttle.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (settings.RateLimitBlockedUntilUtc is { } gateBlockedUntil && gateBlockedUntil > DateTime.UtcNow)
                throw CreateCooldownException(gateBlockedUntil, settings.RateLimitCooldownSource);

            var remainingDelay = minimumRequestInterval - (DateTime.UtcNow - throttle.LastRequestUtc);
            if (remainingDelay > TimeSpan.Zero)
                await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);

            throttle.LastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            throttle.Gate.Release();
        }

        // 엔드포인트가 절대 주소면(BaseUrl과 다른 호스트, 예: fin.land.naver.com) 그대로 사용한다.
        var requestUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? path
            : _apiConfiguration.BaseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        ApplyConfiguredHeaders(request, endpointConfiguration);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
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
        IReadOnlyDictionary<string, string> overrides,
        string? endpoint = null)
    {
        var parameters = new Dictionary<string, string>(profile.Params, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides) parameters[pair.Key] = pair.Value;
        var query = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"));
        return $"{endpoint ?? profile.Endpoint}?{query}";
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
        _listingThrottle.Dispose();
        _rankingThrottle.Dispose();
        _articleDetailThrottle.Dispose();
        _complexDetailThrottle.Dispose();
        _advertisingThrottle.Dispose();
        _rankingConcurrency.Dispose();
        _httpClient.Dispose();
    }

    private sealed class RequestThrottle : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime LastRequestUtc { get; set; } = DateTime.MinValue;

        public void Dispose() => Gate.Dispose();
    }
}

public sealed record ListingLoadProgress(int Page, int ListingCount);
