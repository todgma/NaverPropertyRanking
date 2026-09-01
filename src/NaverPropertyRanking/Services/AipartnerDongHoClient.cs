using System.Net;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 이실장에서 매물번호로 동·호를 읽어 온다.
/// 매물광고 화면의 매물번호 검색을 그대로 흉내낸다. 응답이 JSON이 아니라 화면 전체 HTML이라
/// 결과 표에서 네이버 매물번호가 맞는 행을 찾아 상세주소를 읽는다.
/// </summary>
public sealed class AipartnerDongHoClient : IDongHoLookup
{
    private const string ListPageUrl = "https://www.aipartner.com/offerings/ad_list";
    private const string HomeUrl = "https://www.aipartner.com/home";

    /// <summary>결과 표의 행. 여기 안에서만 매물번호와 상세주소를 찾는다.</summary>
    private static readonly Regex RowPattern = new(
        @"<tr\b[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>행 안의 네이버 매물번호.</summary>
    private static readonly Regex NaverNoPattern = new(
        @"class\s*=\s*[""'][^""']*\bnumberN\b[^""']*[""'][^>]*>(?<value>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>행 안의 단지명·동·호. 법정동은 별도 칸(dongInfo)이라 섞이지 않는다.</summary>
    private static readonly Regex FullNamePattern = new(
        @"class\s*=\s*[""'][^""']*\bfullName\b[^""']*[""'][^>]*>(?<value>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CsrfPattern = new(
        @"<meta[^>]*name\s*=\s*[""']csrf-token[""'][^>]*content\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DigitPattern = new(@"\d+", RegexOptions.Compiled);

    private readonly CpAccount _account;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string _csrfToken = string.Empty;
    private bool _loggedIn;
    private bool _disposed;

    public string CpName => "이실장";

    public AipartnerDongHoClient(CpAccount account, HttpMessageHandler? handler = null)
    {
        _account = account;
        handler ??= new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
    }

    public async Task<CpLoginTestResult> EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loggedIn) return CpLoginTestResult.Ok("이미 로그인되어 있습니다.");
            var site = CpSite.Find(_account.CpValue);
            if (site is null) return CpLoginTestResult.Fail($"지원하지 않는 CP입니다: {_account.CpValue}");

            var result = await CpLoginTester
                .TestWithClientAsync(_client, site, _account, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success) return result;

            // 조회 요청에 붙일 보안 토큰은 로그인 뒤 화면에서 새로 읽는다.
            var (token, landedOn) = await ReadCsrfTokenAsync(cancellationToken).ConfigureAwait(false);

            // 첫 시도가 비면 홈을 한 번 거친 뒤 다시 본다.
            // 로그인 직후 첫 화면에서 세션이 확정되는 경우가 있어서다.
            if (token.Length == 0)
            {
                using (var home = await _client.GetAsync(HomeUrl, cancellationToken).ConfigureAwait(false))
                {
                    _ = home.IsSuccessStatusCode;
                }
                (token, landedOn) = await ReadCsrfTokenAsync(cancellationToken).ConfigureAwait(false);
            }

            _csrfToken = token;
            // 여기서 실패하면 로그인은 통과했는데 세션이 붙지 않은 것이라, 어디로 갔는지 함께 알린다.
            if (_csrfToken.Length == 0)
                return CpLoginTestResult.Fail($"로그인 후 매물광고 화면을 열지 못했습니다(도착: {landedOn}).");

            _loggedIn = true;
            return result;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public async Task<DongHo> GetDongHoAsync(string articleNo, CancellationToken cancellationToken)
    {
        if (!_loggedIn || string.IsNullOrWhiteSpace(articleNo)) return DongHo.Empty;

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_token"] = _csrfToken,
                ["mode"] = "search",
                ["page"] = "1",
                // 화면에 보이는 입력란 이름은 seq지만 실제로 보내는 값은 sr_seq다.
                ["sr_seq"] = articleNo.Trim()
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ListPageUrl) { Content = content };
            request.Headers.Referrer = new Uri(ListPageUrl);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return DongHo.Empty;

            // 로그인이 풀리면 결과 대신 로그인 화면으로 넘어간다.
            // 본문에는 로그인 링크가 늘 섞여 있어 최종 주소로 판단해야 한다.
            if (IsLoginPage(response))
            {
                _loggedIn = false;
                return DongHo.Empty;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseDongHo(html, articleNo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DongHo.Empty;
        }
    }

    /// <summary>
    /// 결과 표에서 매물번호가 일치하는 행의 상세주소를 찾아 동·호를 읽는다.
    /// 매물번호로 검색하므로 보통 한 행이지만, 화면 곳곳에 안내용 표가 섞여 있어
    /// 번호가 맞는 행만 골라야 엉뚱한 값을 읽지 않는다.
    /// </summary>
    public static DongHo ParseDongHo(string html, string articleNo)
    {
        var wanted = DigitPattern.Match(articleNo ?? string.Empty).Value;
        if (wanted.Length == 0) return DongHo.Empty;

        foreach (Match row in RowPattern.Matches(html ?? string.Empty))
        {
            var content = row.Groups["row"].Value;
            var numberMatch = NaverNoPattern.Match(content);
            if (!numberMatch.Success) continue;

            var number = DigitPattern.Match(DongHoParser.Normalize(numberMatch.Groups["value"].Value)).Value;
            if (!string.Equals(number, wanted, StringComparison.Ordinal)) continue;

            var nameMatch = FullNamePattern.Match(content);
            if (!nameMatch.Success) return DongHo.Empty;
            return DongHoParser.ParseAddress(DongHoParser.Normalize(nameMatch.Groups["value"].Value));
        }

        return DongHo.Empty;
    }

    /// <summary>
    /// 매물광고 화면을 열어 조회에 쓸 보안 토큰을 읽는다.
    /// 로그인이 제대로 붙지 않았으면 여기서 로그인 화면으로 넘어가므로 함께 걸러진다.
    /// </summary>
    private async Task<(string Token, string LandedOn)> ReadCsrfTokenAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(ListPageUrl, cancellationToken).ConfigureAwait(false);
        var landedOn = response.RequestMessage?.RequestUri?.ToString() ?? ListPageUrl;
        CpLoginTrace.Write($"매물광고 화면 열기 · HTTP {(int)response.StatusCode} · 도착 {landedOn}");
        if (!response.IsSuccessStatusCode) return (string.Empty, $"HTTP {(int)response.StatusCode}");
        if (IsLoginPage(response)) return (string.Empty, "로그인 화면");

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = CsrfPattern.Match(html);
        return match.Success
            ? (WebUtility.HtmlDecode(match.Groups["value"].Value), landedOn)
            : (string.Empty, $"토큰 없음 · {landedOn}");
    }

    /// <summary>응답이 로그인 화면으로 넘어갔는지 최종 주소로 판단한다.</summary>
    private static bool IsLoginPage(HttpResponseMessage response) =>
        (response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty)
            .Contains("/integrated/login", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginGate.Dispose();
        _client.Dispose();
    }
}
