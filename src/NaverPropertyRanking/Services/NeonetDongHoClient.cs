using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 부동산뱅크(중개회원 관리자)에서 매물번호로 동·호를 읽어 온다.
/// 매물 목록 화면이 주소에 검색어를 그대로 담는 GET이라 보안 토큰 없이 조회할 수 있다.
/// 화면이 EUC-KR이라 응답은 바이트로 받아 직접 해석한다.
/// </summary>
public sealed class NeonetDongHoClient : IDongHoLookup
{
    private const string ListUrl =
        "https://agency.neonet.co.kr/novo-agency/view/offerings/NaverOfferingsList.neo";

    /// <summary>로그인 뒤 중개회원 화면으로 넘어가야 목록을 볼 수 있다.</summary>
    private const string AgencyReturnUrl =
        "https://agency.neonet.co.kr/novo-agency/view/offerings/OfferingsIndex.neo";

    private const string LoginActionUrl =
        "https://www.neonet.co.kr/novo-rebank/view/login/ptl.login_after.usr.neo";

    /// <summary>목록 표의 행.</summary>
    private static readonly Regex RowPattern = new(
        @"<tr\b[^>]*class\s*=\s*[""'][^""']*\bbbs_w\b[^""']*[""'][^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>행 안의 각 칸.</summary>
    private static readonly Regex CellPattern = new(
        @"<td\b[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// 행이 어느 네이버 매물번호인지.
    /// 매물번호 칸은 '뱅크번호 (네이버번호)' 형태라 괄호 안 숫자를 본다.
    /// 서비스중인 매물만 이 번호가 링크로 감싸여 있어 태그를 걷어낸 뒤에 찾아야 한다.
    /// </summary>
    private static readonly Regex NaverNoPattern = new(
        @"\(\s*(?<value>\d{6,})\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex DigitPattern = new(@"\d+", RegexOptions.Compiled);

    private readonly CpAccount _account;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private bool _loggedIn;
    private bool _disposed;

    public string CpName => "부동산뱅크";

    static NeonetDongHoClient()
    {
        // EUC-KR은 .NET 기본 인코딩 목록에 없어 따로 등록해야 한다.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public NeonetDongHoClient(CpAccount account, HttpMessageHandler? handler = null)
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

    /// <summary>
    /// 로그인 폼은 일반 화면과 같지만, 돌아갈 주소를 중개회원 화면으로 지정해야
    /// 그쪽 세션까지 이어진다. 로그인 뒤 목록 화면이 실제로 열리는지로 성공을 확인한다.
    /// </summary>
    public async Task<CpLoginTestResult> EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loggedIn) return CpLoginTestResult.Ok("이미 로그인되어 있습니다.");
            if (string.IsNullOrWhiteSpace(_account.UserId) || string.IsNullOrEmpty(_account.Password))
                return CpLoginTestResult.Fail("아이디와 패스워드를 먼저 저장해 주세요.");

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["return_url"] = AgencyReturnUrl,
                ["pop"] = "N",
                ["cyber"] = string.Empty,
                ["homepage"] = string.Empty,
                ["ssl"] = "ok",
                ["id"] = _account.UserId,
                ["pw"] = _account.Password
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginActionUrl) { Content = content };
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)response.StatusCode}).");

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var verdict = CpLoginTester.ReadNeonetResult(body);
            if (!verdict.Success) return verdict;

            // 로그인 성공이어도 중개회원 화면 권한이 없을 수 있어 목록 화면을 실제로 열어 본다.
            var probe = await LoadListAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            if (probe is null || probe.Contains("MemberLogin", StringComparison.OrdinalIgnoreCase))
                return CpLoginTestResult.Fail("중개회원 매물 화면을 열지 못했습니다. 중개회원 계정인지 확인해 주세요.");

            _loggedIn = true;
            return CpLoginTestResult.Ok("로그인에 성공했습니다.");
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
            var html = await LoadListAsync(articleNo.Trim(), cancellationToken).ConfigureAwait(false);
            if (html is null) return DongHo.Empty;
            if (html.Contains("MemberLogin", StringComparison.OrdinalIgnoreCase))
            {
                _loggedIn = false;
                return DongHo.Empty;
            }
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
    /// 목록 화면에서 매물번호가 맞는 행을 찾아 동·호 칸을 읽는다.
    /// 소재지 칸에도 '가능동' 같은 법정동이 있지만 숫자가 없어 동·호 규칙에 걸리지 않는다.
    /// </summary>
    public static DongHo ParseDongHo(string html, string articleNo)
    {
        var wanted = DigitPattern.Match(articleNo ?? string.Empty).Value;
        if (wanted.Length == 0) return DongHo.Empty;

        foreach (Match row in RowPattern.Matches(html ?? string.Empty))
        {
            var cells = CellPattern.Matches(row.Groups["row"].Value)
                .Select(cell => DongHoParser.Normalize(cell.Groups["cell"].Value))
                .ToList();

            var number = cells
                .Select(cell => NaverNoPattern.Match(cell))
                .FirstOrDefault(match => match.Success)?.Groups["value"].Value;
            if (!string.Equals(number, wanted, StringComparison.Ordinal)) continue;

            // 동·호는 전용 칸에 있다. 칸을 하나씩 보며 값이 나오는 첫 칸을 쓴다.
            // 소재지 칸의 법정동은 숫자가 없어 동·호 규칙에 걸리지 않는다.
            foreach (var cell in cells)
            {
                var value = DongHoParser.ParseAddress(cell);
                if (value.HasValue) return value;
            }
            return DongHo.Empty;
        }

        return DongHo.Empty;
    }

    private async Task<string?> LoadListAsync(string searchText, CancellationToken cancellationToken)
    {
        var url = $"{ListUrl}?search_type=total&search_text={Uri.EscapeDataString(searchText)}";
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>EUC-KR로 오는 응답을 바이트에서 직접 해석한다.</summary>
    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        var encoding = ResolveEncoding(charset);
        return encoding.GetString(bytes);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* 모르는 이름이면 아래 기본값을 쓴다. */ }
        }
        try { return Encoding.GetEncoding("euc-kr"); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginGate.Dispose();
        _client.Dispose();
    }
}
