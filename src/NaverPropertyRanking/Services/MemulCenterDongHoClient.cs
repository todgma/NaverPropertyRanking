using System.Net;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 매물관리센터 계열(선방·우리집부동산) 사이트 한 곳의 주소 구성.
/// 화면 구조가 같고 주소만 달라 설정으로 분리했다.
/// </summary>
public sealed record MemulCenterSite(
    string CpName,
    string BaseUrl,
    IReadOnlyList<(string Name, string Path, string Extra)> ListPages)
{
    /// <summary>로그인 폼이 스크립트로 지정하는 전송 주소.</summary>
    public string LoginActionUrl => $"{BaseUrl}/member/loginrun.asp";

    public static MemulCenterSite Sunbang { get; } = new(
        "선방",
        "http://homesdid.co.kr/mmc",
        [
            ("등록매물", "memul/memulList.asp", "srch_order_flag=1&page_chart=O"),
            ("등록종료", "memul/memulendlist.asp", "srch_order_flag=3&page_chart=")
        ]);

    public static MemulCenterSite WooriHouse { get; } = new(
        "우리집 부동산",
        "https://woori-house.co.kr/new_mmc",
        [
            ("확인매물", "memul/mm_list.asp", "srch_order_flag=1&action_url_val=mm_list.asp")
        ]);

    public static MemulCenterSite? Find(string? cpValue) => cpValue switch
    {
        "5" => Sunbang,
        "6" => WooriHouse,
        _ => null
    };
}

/// <summary>
/// 매물관리센터 계열 사이트에서 매물번호로 동·호를 읽어 온다.
///
/// 동·호가 목록 행에 바로 나오므로 상세 화면까지 들어가지 않는다.
/// 사이트마다 표기가 조금 달라 두 가지를 모두 본다.
///  - 전용 칸: &lt;span class="dngh"&gt;101동 103호&lt;/span&gt;
///  - 주소 칸: "용산구 한강로1가 / 용산파크자이 / A동 602호 (6층)"
/// </summary>
public sealed class MemulCenterDongHoClient : IDongHoLookup
{
    /// <summary>목록이 요구하는 기본 조건. 화면이 보내는 값과 같다.</summary>
    private const string BaseQuery =
        "s_step=&mm_uid=&url=&newimgChk=&currentpage=1&s_orderby=&s_orderby2=&s_rlsttype_cd=" +
        "&s_dealtype_cd=&srch_mm_send_flag=&VRFC_TYPE=&s_area=&DONG_NM=&ADR_HO=&ADR_BUNJI=" +
        "&schSpcSel=&schminSpc=&schmaxSpc=&s_startdate=&s_enddate=&s_proc=&asil_mm_flag=" +
        "&s_viewCount=15&excel_flag=&srch_trash_flag=&srch_mm_view=N&copy_flag=" +
        "&initialization=&vrfc_d_pop=";

    /// <summary>목록 행의 상세 링크. 이 링크가 있는 칸이 주소 칸이다.</summary>
    private static readonly Regex ViewMemulPattern = new(
        @"viewMemul\(\s*'(?<value>\d+)'\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>상세 링크와 그 안의 글. 주소는 이 링크 안에만 있고 메모·버튼은 바깥에 있다.</summary>
    private static readonly Regex AddressLinkPattern = new(
        @"<a\b[^>]*viewMemul\(\s*'(?<no>\d+)'\s*\)[^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>동·호 전용 칸. 우리집부동산이 이 형태로 준다.</summary>
    private static readonly Regex DongHoSpanPattern = new(
        @"<span[^>]*class\s*=\s*[""'][^""']*\bdngh\b[^""']*[""'][^>]*>(?<value>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>행 안의 각 칸.</summary>
    private static readonly Regex CellPattern = new(
        @"<td\b[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly CpAccount _account;
    private readonly MemulCenterSite _site;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private bool _loggedIn;
    private bool _disposed;

    public string CpName => _site.CpName;

    public MemulCenterDongHoClient(CpAccount account, MemulCenterSite site, HttpMessageHandler? handler = null)
    {
        _account = account;
        _site = site;
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
            var number = articleNo.Trim();
            var search = Uri.EscapeDataString(number);

            foreach (var (name, path, extra) in _site.ListPages)
            {
                var html = await GetAsync(
                    $"{_site.BaseUrl}/{path}?{BaseQuery}&{extra}&s_mm_uid={search}",
                    cancellationToken).ConfigureAwait(false);
                if (html is null) continue;

                var value = ParseDongHo(html);
                CpLoginTrace.Write(
                    $"{_site.CpName} {name} 조회 · 매물번호 {number} · 동 [{value.Dong}] 호 [{value.Ho}]");
                if (value.HasValue) return value;
            }

            // 이 CP에 없는 매물은 그냥 넘어간다. 다른 CP 계정이 있으면 그쪽에서 채운다.
            return DongHo.Empty;
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
    /// 목록 행에서 동·호를 읽는다.
    /// 전용 칸(dngh)이 있으면 그것이 가장 정확하고, 없으면 주소 칸의 글에서 찾는다.
    /// 주소 칸은 상세 링크(viewMemul)가 함께 있는 칸으로 알아본다.
    /// </summary>
    public static DongHo ParseDongHo(string html)
    {
        var text = html ?? string.Empty;

        var span = DongHoSpanPattern.Match(text);
        if (span.Success)
        {
            var fromSpan = DongHoParser.ParseAddress(DongHoParser.Normalize(span.Groups["value"].Value));
            if (fromSpan.HasValue) return fromSpan;
        }

        // 같은 칸에 메모·버튼 글자가 함께 들어 있어 링크 안쪽만 읽는다.
        foreach (Match link in AddressLinkPattern.Matches(text))
        {
            var value = DongHoParser.ParseAddress(DongHoParser.Normalize(link.Groups["text"].Value));
            if (value.HasValue) return value;
        }

        // 링크가 없는 화면을 대비해 상세 링크가 있는 칸도 한 번 더 본다.
        foreach (Match cell in CellPattern.Matches(text))
        {
            var content = cell.Groups["cell"].Value;
            if (!ViewMemulPattern.IsMatch(content)) continue;

            var value = DongHoParser.ParseAddress(DongHoParser.Normalize(content));
            if (value.HasValue) return value;
        }

        return DongHo.Empty;
    }

    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        // 로그인이 풀리면 목록 대신 안내창을 띄우는 짧은 화면이 돌아온다.
        if (body.Contains("로그인후 이용하세요", StringComparison.Ordinal))
        {
            _loggedIn = false;
            return null;
        }
        return body;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginGate.Dispose();
        _client.Dispose();
    }
}
