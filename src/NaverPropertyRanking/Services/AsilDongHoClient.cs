using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 아실에서 매물번호로 동·호를 읽어 온다.
///
/// 아실은 두 번 물어봐야 한다.
///  1) 목록(등록매물·등록종료)에서 네이버 매물번호로 찾아 아실 매물번호(mm_uid)를 얻고
///  2) 그 번호로 상세 화면을 열어 매물명에서 동·호를 읽는다.
/// 로그인 주소가 계정마다 다르지만 매물 화면은 realty.asil.kr로 공통이다.
/// </summary>
public sealed class AsilDongHoClient : IDongHoLookup
{
    // 상세 화면. 매물명에 동·호가 함께 들어 있어 재등록 폼보다 읽기 쉽다.
    private const string DetailUrl = "https://realty.asil.kr/mmc/memul/memulStep.asp";

    /// <summary>목록·상세 화면이 함께 요구하는 기본 조건. 화면이 보내는 값과 같다.</summary>
    private const string BaseQuery =
        "s_step=&url=&newimgChk=&currentpage=1&s_orderby=&s_orderby2=&s_rlsttype_cd=" +
        "&s_dealtype_cd=&VRFC_TYPE=&s_area=&DONG_NM=&ADR_HO=&ADR_BUNJI=&schSpcSel=" +
        "&schminSpc=&schmaxSpc=&s_startdate=&s_enddate=&s_proc=" +
        "&s_viewCount=20&excel_flag=&srch_trash_flag=&srch_mm_view=N&copy_flag=";

    /// <summary>
    /// 매물이 있을 수 있는 목록. 두 화면 모두 매물번호 입력란 이름이 s_mm_uid로 같다.
    /// 등록매물에서 못 찾으면 등록종료 목록에서 한 번 더 찾는다.
    /// 매물번호로 지목해 찾으므로 아실 매물만 거르는 조건(asil_mm_flag)은 걸지 않는다.
    /// 조건을 더 걸면 찾을 수 있는 매물까지 걸러진다.
    /// </summary>
    private static readonly (string Name, string Url, string Extra)[] ListPages =
    [
        ("등록매물", "https://realty.asil.kr/mmc/memul/memulList.asp",
            "asil_mm_flag=&srch_order_flag=1&page_chart=O"),
        ("등록종료", "https://realty.asil.kr/mmc/memul/memulendlist.asp",
            "asil_mm_flag=&srch_order_flag=3&page_chart=")
    ];

    /// <summary>목록 행의 상세 링크. 괄호 안이 아실 매물번호다.</summary>
    private static readonly Regex ViewMemulPattern = new(
        @"viewMemul\(\s*'(?<value>\d+)'\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>상세 링크와 그 안의 글. 주소는 이 링크 안에만 있고 메모·버튼은 바깥에 있다.</summary>
    private static readonly Regex AddressLinkPattern = new(
        @"<a\b[^>]*viewMemul\(\s*'(?<no>\d+)'\s*\)[^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>주소를 통해 넘어가는 경우의 아실 매물번호.</summary>
    private static readonly Regex AsilNoPattern = new(
        @"[?&]mm_uid=(?<value>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>목록 행의 각 칸.</summary>
    private static readonly Regex CellPattern = new(
        @"<td\b[^>]*>(?<cell>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>콤보에서 선택된 항목. 숨은 입력란이 비었을 때만 쓴다.</summary>
    private static readonly Regex SelectedOptionPattern = new(
        @"<option[^>]*\bselected\b[^>]*>(?<text>.*?)</option>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DigitPattern = new(@"\d+", RegexOptions.Compiled);

    /// <summary>매물 화면(realty.asil.kr)으로 넘어가는 주소. 로그인 뒤 화면 어딘가에 들어 있다.</summary>
    private static readonly Regex MemulCenterLinkPattern = new(
        @"(?<url>(?:https?:)?//[A-Za-z0-9._\-]*realty\.asil\.kr/[^\s""'<>()\\]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>인증코드를 내려 주는 주소. 화면 스크립트가 이 주소를 먼저 부른다.</summary>
    private static readonly Regex AuthEndpointPattern = new(
        @"(?<url>/[^\s'""<>]*auth\.jsp\?auth=[^\s'""<>&]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>매물 화면이 요구하는 열쇠 값. 로그인할 때마다 새로 발급된다.</summary>
    private static readonly Regex SsoKeyPattern = new(
        @"sso_key=(?<value>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>인증코드 응답. {"auth":"..."} 형태로 돌아온다.</summary>
    private static readonly Regex AuthValuePattern = new(
        @"""auth""\s*:\s*""(?<value>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>메뉴가 프레임 안에 들어 있는 화면이 있어 한 겹 더 본다.</summary>
    private static readonly Regex FramePattern = new(
        @"<(?:frame|iframe)\b[^>]*\bsrc\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>화면이 스크립트로 다음 주소로 옮겨 갈 때 쓰는 표현.</summary>
    private static readonly Regex MovePattern = new(
        @"location\.(?:href|replace)\s*(?:=|\()\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>로그인 뒤 화면 이동을 따라가는 최대 횟수.</summary>
    private const int MaxLoginHops = 4;

    /// <summary>매물관리 링크를 찾으려고 훑는 화면 수.</summary>
    private const int MaxMemulCenterPages = 8;

    /// <summary>매물관리 링크를 실제로 열어 보는 횟수.</summary>
    private const int MaxMemulCenterLinks = 5;

    /// <summary>sso 진입 코드를 만드는 자리를 찾을 때 보는 글자.</summary>
    private static readonly string[] SsoKeywords = ["sso_code", "login_sso_act", "sso_key"];

    /// <summary>기록에 남기는 sso 조각 수.</summary>
    private const int MaxSsoDumps = 6;

    /// <summary>매물 화면의 기준 주소. 쿠키를 확인·연결할 때 쓴다.</summary>
    private static readonly Uri MemulCenterRoot = new("https://realty.asil.kr/");

    private readonly CpAccount _account;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private bool _loggedIn;
    private bool _disposed;

    public string CpName => "아실";

    static AsilDongHoClient()
    {
        // 오래된 화면이 EUC-KR로 오는 경우가 있어 코드페이지를 등록해 둔다.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public AsilDongHoClient(CpAccount account, HttpMessageHandler? handler = null)
    {
        _account = account;
        handler ??= new HttpClientHandler
        {
            CookieContainer = _cookies,
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
    /// 로그인한다.
    /// 세션 쿠키가 로그인 도메인(계정별 주소)에만 걸리고 매물 화면(realty.asil.kr)에는 붙지 않는다.
    /// 그래서 화면이 하는 것처럼 로그인 직후의 이동을 따라가 매물 쪽 세션까지 만든다.
    /// </summary>
    public async Task<CpLoginTestResult> EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loggedIn) return CpLoginTestResult.Ok("이미 로그인되어 있습니다.");
            if (_account.LoginUrl.Length == 0)
                return CpLoginTestResult.Fail("주소로 쓸 아이디를 먼저 저장해 주세요.");
            if (string.IsNullOrEmpty(_account.Password))
                return CpLoginTestResult.Fail("저장된 비밀번호를 읽지 못했습니다. 다시 저장해 주세요.");

            using var pageResponse = await _client.GetAsync(_account.LoginUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!pageResponse.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 페이지를 열지 못했습니다(HTTP {(int)pageResponse.StatusCode}).");

            var loginPageUrl = pageResponse.RequestMessage?.RequestUri ?? new Uri(_account.LoginUrl);
            var actionUrl = new Uri(loginPageUrl, "loginProcess.jsp");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["p"] = _account.Password
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
            request.Headers.Referrer = loginPageUrl;
            using var loginResponse = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!loginResponse.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

            var body = await ReadBodyAsync(loginResponse, cancellationToken).ConfigureAwait(false);
            var verdict = CpLoginTester.ReadAsilResult(body);
            CpLoginTrace.Write($"아실 로그인 · 성공 {verdict.Success} · {verdict.Message}");
            if (!verdict.Success) return verdict;

            // 로그인 성공 화면은 곧바로 다음 주소로 옮겨 간다.
            var landedUrl = loginResponse.RequestMessage?.RequestUri ?? actionUrl;
            var visited = await FollowRedirectsAsync(body, landedUrl, cancellationToken)
                .ConfigureAwait(false);

            // 로그인 도메인의 세션은 매물 화면(realty.asil.kr)에 붙지 않는다.
            // 화면이 하는 것처럼 매물관리 링크를 눌러 그쪽 세션까지 만든다.
            if (!await OpenViaSsoAsync(visited, cancellationToken).ConfigureAwait(false))
            {
                // 정식 진입이 막히면 화면에 있는 링크를 훑어 보고, 그래도 안 되면 쿠키라도 이어 본다.
                await OpenMemulCenterAsync(visited, cancellationToken).ConfigureAwait(false);
                BridgeSession(loginPageUrl);
            }

            // 매물 화면이 실제로 열리는지 확인한다. 열리지 않으면 조회해도 빈손이다.
            var probe = await GetAsync(
                $"{ListPages[0].Url}?{BaseQuery}&{ListPages[0].Extra}&mm_uid=&s_mm_uid=",
                cancellationToken).ConfigureAwait(false);
            if (probe is null)
                return CpLoginTestResult.Fail("로그인 후 매물 화면을 열지 못했습니다. 아실에서 매물관리 권한을 확인해 주세요.");
            CpLoginTrace.Write($"아실 매물 화면 열림 · 길이 {probe.Length}");

            _loggedIn = true;
            return CpLoginTestResult.Ok("로그인에 성공했습니다.");
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// 화면이 스크립트로 옮겨 가는 주소를 따라간다.
    /// 도메인이 바뀌는 이동이 있어야 매물 화면 쪽에도 세션이 생긴다.
    /// </summary>
    private async Task<List<(Uri Url, string Body)>> FollowRedirectsAsync(
        string html,
        Uri baseUrl,
        CancellationToken cancellationToken)
    {
        var current = baseUrl;
        var body = html;
        var visited = new List<(Uri, string)> { (current, body) };

        for (var hop = 0; hop < MaxLoginHops; hop++)
        {
            var move = MovePattern.Match(body);
            if (!move.Success) break;
            if (!Uri.TryCreate(current, WebUtility.HtmlDecode(move.Groups["url"].Value), out var next)) break;
            // 로그인 화면으로 되돌아가는 이동은 따라가 봐야 소용이 없다.
            if (next.AbsolutePath.Contains("login.jsp", StringComparison.OrdinalIgnoreCase)) break;

            using var response = await _client.GetAsync(next, cancellationToken).ConfigureAwait(false);
            current = response.RequestMessage?.RequestUri ?? next;
            body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            visited.Add((current, body));
            CpLoginTrace.Write($"아실 로그인 이동 {hop + 1}회 · 요청 {next} · 도착 {current}");

            // 매물관리 진입 스크립트가 있는 화면이 주 화면이다. 여기서 멈춘다.
            if (body.Contains("login_sso_act", StringComparison.OrdinalIgnoreCase)) break;
        }

        return visited;
    }

    /// <summary>
    /// 화면이 하는 그대로 매물관리에 진입한다.
    ///
    /// 로그인 화면의 스크립트는 두 걸음을 밟는다.
    ///  1) /member_adm/naver/auth.jsp?auth=... 를 불러 인증코드를 받고
    ///  2) 그 코드를 sso_code에 넣어 realty.asil.kr의 진입 주소를 연다.
    /// 열쇠(sso_key)와 인증코드는 로그인할 때마다 새로 발급되므로 화면에서 그때그때 읽는다.
    /// </summary>
    private async Task<bool> OpenViaSsoAsync(
        IReadOnlyList<(Uri Url, string Body)> pages,
        CancellationToken cancellationToken)
    {
        foreach (var (url, html) in pages)
        {
            var endpoint = AuthEndpointPattern.Match(html);
            var key = SsoKeyPattern.Match(html);
            if (!endpoint.Success || !key.Success) continue;
            if (!Uri.TryCreate(url, WebUtility.HtmlDecode(endpoint.Groups["url"].Value), out var authUrl)) continue;

            string authCode;
            try
            {
                // 화면에서 버튼을 누른 것과 같은 자리에서 부른다. 어디서 왔는지를 함께 알린다.
                using var authRequest = new HttpRequestMessage(HttpMethod.Get, authUrl);
                authRequest.Headers.Referrer = url;
                authRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
                using var authResponse = await _client.SendAsync(authRequest, cancellationToken).ConfigureAwait(false);
                var authBody = await ReadBodyAsync(authResponse, cancellationToken).ConfigureAwait(false);
                CpLoginTrace.Write(
                    $"아실 인증코드 요청 · HTTP {(int)authResponse.StatusCode} · {authUrl} · 내용 {Preview(authBody)}");

                var auth = AuthValuePattern.Match(authBody);
                if (!auth.Success || auth.Groups["value"].Value.Length == 0) continue;
                authCode = auth.Groups["value"].Value;
            }
            catch (HttpRequestException ex)
            {
                CpLoginTrace.Write($"아실 인증코드 요청 실패 · {ex.Message} · {authUrl}");
                continue;
            }

            // 화면 스크립트가 값을 그대로 이어 붙이므로 여기서도 손대지 않는다.
            var ssoUrl =
                $"{MemulCenterRoot}mmc/sso/login_sso_act.asp?autologin=false" +
                $"&sso_key={key.Groups["value"].Value}&sso_code={authCode}";

            try
            {
                using var ssoRequest = new HttpRequestMessage(HttpMethod.Get, ssoUrl);
                ssoRequest.Headers.Referrer = url;
                using var ssoResponse = await _client.SendAsync(ssoRequest, cancellationToken).ConfigureAwait(false);
                var ssoBody = await ReadBodyAsync(ssoResponse, cancellationToken).ConfigureAwait(false);
                CpLoginTrace.Write(
                    $"아실 sso 진입 · HTTP {(int)ssoResponse.StatusCode} · 길이 {ssoBody.Length} · {ssoUrl}" +
                    $" · 내용 {Preview(ssoBody)}");

                // 진입 화면은 곧바로 매물 화면으로 옮겨 간다. 그 이동이 세션을 만든다.
                await FollowRedirectsAsync(
                    ssoBody,
                    ssoResponse.RequestMessage?.RequestUri ?? new Uri(ssoUrl),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                CpLoginTrace.Write($"아실 sso 진입 실패 · {ex.Message} · {ssoUrl}");
                continue;
            }

            var cookies = _cookies.GetCookies(MemulCenterRoot);
            if (cookies.Count == 0) continue;

            CpLoginTrace.Write($"아실 매물관리 세션 생성됨 · [{Describe(cookies)}]");
            return true;
        }

        CpLoginTrace.Write("아실 sso 진입 못 함 · 화면에서 인증코드 주소나 열쇠를 찾지 못했다.");
        return false;
    }

    /// <summary>
    /// 매물관리 화면으로 넘어가 그쪽 세션을 만든다.
    ///
    /// 로그인 뒤 화면 어딘가에 realty.asil.kr로 넘어가는 링크가 있고,
    /// 그 링크를 한 번 열어 줘야 매물 화면에서도 로그인 상태가 된다.
    /// 화면 구성이 계정마다 조금씩 달라 로그인 흐름에서 본 화면과 그 안의 프레임까지 훑는다.
    /// </summary>
    private async Task OpenMemulCenterAsync(
        IReadOnlyList<(Uri Url, string Body)> visited,
        CancellationToken cancellationToken)
    {
        var pages = new List<(Uri Url, string Body)>(visited);

        // 메뉴가 프레임 안에 들어 있는 화면이 있어 한 겹 더 연다.
        foreach (var (url, html) in visited)
        {
            foreach (Match frame in FramePattern.Matches(html))
            {
                if (pages.Count >= MaxMemulCenterPages) break;
                if (!Uri.TryCreate(url, WebUtility.HtmlDecode(frame.Groups["url"].Value), out var next)) continue;
                if (IsStaticAsset(next)) continue;

                try
                {
                    using var response = await _client.GetAsync(next, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    pages.Add((next, await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)));
                }
                catch (HttpRequestException) { /* 열리지 않는 프레임은 넘어간다. */ }
            }
        }

        var candidates = new List<string>();
        foreach (var (_, html) in pages)
        {
            foreach (Match link in MemulCenterLinkPattern.Matches(html))
            {
                var found = WebUtility.HtmlDecode(link.Groups["url"].Value);
                if (found.StartsWith("//", StringComparison.Ordinal)) found = "https:" + found;
                if (!Uri.TryCreate(found, UriKind.Absolute, out var uri)) continue;
                if (IsStaticAsset(uri)) continue;
                if (candidates.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)) continue;
                candidates.Add(uri.AbsoluteUri);
            }
        }

        // sso 진입에 필요한 코드가 화면 어딘가에서 만들어진다. 그 자리를 그대로 남긴다.
        DumpSsoContext(pages);

        // 세션을 만들어 주는 것은 sso 진입 주소다. 안내 화면보다 먼저 연다.
        candidates = candidates
            .OrderByDescending(url => url.Contains("/sso/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        CpLoginTrace.Write(
            $"아실 매물관리 링크 {candidates.Count}개 · 훑은 화면 {pages.Count}개" +
            (candidates.Count == 0
                ? string.Empty
                : " · " + string.Join(" | ", candidates.Take(MaxMemulCenterLinks))));

        foreach (var candidate in candidates.Take(MaxMemulCenterLinks))
        {
            try
            {
                using var response = await _client.GetAsync(candidate, cancellationToken).ConfigureAwait(false);
                var entryBody = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var landed = response.RequestMessage?.RequestUri ?? new Uri(candidate);
                CpLoginTrace.Write(
                    $"아실 매물관리 진입 · HTTP {(int)response.StatusCode} · 길이 {entryBody.Length} · {candidate}" +
                    $" · 내용 {Preview(entryBody)}");

                // 진입 화면은 곧바로 다음 주소로 옮겨 간다. 그 이동이 매물 쪽 세션을 만든다.
                await FollowRedirectsAsync(entryBody, landed, cancellationToken).ConfigureAwait(false);

                // 세션이 생겼으면 더 열어 볼 필요가 없다.
                if (_cookies.GetCookies(MemulCenterRoot).Count > 0)
                {
                    CpLoginTrace.Write($"아실 매물관리 세션 생성됨 · [{Describe(_cookies.GetCookies(MemulCenterRoot))}]");
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                CpLoginTrace.Write($"아실 매물관리 진입 실패 · {ex.Message} · {candidate}");
            }
        }
    }

    /// <summary>
    /// 매물 화면에도 로그인 쿠키가 붙어 있는지 보고, 없으면 로그인 쪽 쿠키를 그대로 붙여 준다.
    ///
    /// 브라우저에서는 로그인해 둔 뒤 매물 주소를 새 창에 붙여 넣어도 그대로 조회된다.
    /// 같은 asil.kr 안에서 세션이 공유된다는 뜻이라, 쿠키가 로그인 주소에만 남았으면 옮겨 준다.
    /// </summary>
    private void BridgeSession(Uri loginUrl)
    {
        var mine = _cookies.GetCookies(loginUrl);
        var theirs = _cookies.GetCookies(MemulCenterRoot);
        CpLoginTrace.Write($"아실 쿠키 · 로그인 [{Describe(mine)}] · 매물 [{Describe(theirs)}]");
        if (theirs.Count > 0 || mine.Count == 0) return;

        foreach (Cookie cookie in mine)
        {
            _cookies.Add(MemulCenterRoot, new Cookie(cookie.Name, cookie.Value, "/", MemulCenterRoot.Host));
        }
        CpLoginTrace.Write($"아실 쿠키 이어붙임 · [{Describe(_cookies.GetCookies(MemulCenterRoot))}]");
    }

    /// <summary>
    /// sso 진입 코드를 만드는 자리를 기록에 남긴다.
    /// 주소만으로는 무엇을 채워야 하는지 알 수 없어 그 앞뒤 스크립트를 함께 본다.
    /// </summary>
    private static void DumpSsoContext(IReadOnlyList<(Uri Url, string Body)> pages)
    {
        var written = 0;
        foreach (var (url, html) in pages)
        {
            foreach (var keyword in SsoKeywords)
            {
                var from = 0;
                while (written < MaxSsoDumps)
                {
                    var at = html.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;

                    var start = Math.Max(0, at - 300);
                    var end = Math.Min(html.Length, at + 400);
                    CpLoginTrace.Write(
                        $"아실 sso 조각[{keyword}] · {url.AbsolutePath} · " +
                        DongHoParser.Normalize(html[start..end]));
                    written++;
                    from = at + keyword.Length;
                }
            }
        }

        if (written == 0) CpLoginTrace.Write("아실 sso 조각 없음 · 화면에서 sso 관련 글자를 찾지 못했다.");
    }

    /// <summary>화면 앞부분만 짧게 남긴다. 기록이 길어지지 않게 한다.</summary>
    private static string Preview(string body)
    {
        var text = DongHoParser.Normalize(body ?? string.Empty);
        return text.Length <= 200 ? text : text[..200];
    }

    private static string Describe(CookieCollection cookies) =>
        string.Join(", ", cookies.Select(cookie => cookie.Name));

    /// <summary>그림·스크립트처럼 세션과 상관없는 주소인지 본다.</summary>
    private static bool IsStaticAsset(Uri url)
    {
        var path = url.AbsolutePath;
        return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>응답 본문을 사이트가 알려 준 문자집합으로 읽는다.</summary>
    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        return Decode(bytes, charset);
    }

    public async Task<DongHo> GetDongHoAsync(string articleNo, CancellationToken cancellationToken)
    {
        if (!_loggedIn || string.IsNullOrWhiteSpace(articleNo)) return DongHo.Empty;

        try
        {
            var number = articleNo.Trim();
            var search = Uri.EscapeDataString(number);

            // 등록매물에 없으면 등록종료 목록도 본다. 종료된 매물도 동·호는 그대로 남아 있다.
            string? asilNo = null;
            var fromList = DongHo.Empty;
            var orderFlag = "1";
            foreach (var (name, url, extra) in ListPages)
            {
                var listHtml = await GetAsync(
                    $"{url}?{BaseQuery}&{extra}&mm_uid=&s_mm_uid={search}",
                    cancellationToken).ConfigureAwait(false);
                if (listHtml is null) continue;

                // 아실번호와 동·호는 같은 행의 상세 링크에서 함께 읽는다.
                // 따로 읽으면 다른 행의 값을 섞어 쓸 수 있다.
                var row = ParseListRow(listHtml, number);
                CpLoginTrace.Write(
                    $"아실 {name} 조회 · 매물번호 {number} · 아실번호 {(row.AsilNo ?? "없음")} " +
                    $"· 동 [{row.Value.Dong}] 호 [{row.Value.Ho}]");
                if (row.AsilNo is null) continue;

                asilNo = row.AsilNo;
                // 목록에도 동·호가 있지만 메모·층수가 섞여 있어 상세 화면을 우선한다.
                fromList = row.Value;
                // 상세 화면은 어느 목록에서 왔는지도 함께 받는다.
                orderFlag = name == "등록종료" ? "3" : "1";
                break;
            }
            // 아실에 없는 매물은 그냥 넘어간다. 다른 CP 계정이 있으면 그쪽에서 채운다.
            if (asilNo is null) return fromList;

            // 상세 화면은 네이버 매물번호가 아니라 목록에서 얻은 아실번호(mm_uid)로 연다.
            var detailUrl =
                $"{DetailUrl}?{BaseQuery}&asil_mm_flag=&srch_order_flag={orderFlag}&page_chart=&mm_uid={asilNo}&s_mm_uid=";
            var detailHtml = await GetAsync(detailUrl, cancellationToken).ConfigureAwait(false);
            if (detailHtml is null)
            {
                CpLoginTrace.Write($"아실 상세 열기 실패 · 아실번호 {asilNo} · {detailUrl}");
                return fromList;
            }

            var value = ParseDongHo(detailHtml);
            CpLoginTrace.Write(
                $"아실 상세 조회 · 아실번호 {asilNo} · 길이 {detailHtml.Length} " +
                $"· 동 [{value.Dong}] 호 [{value.Ho}] · {detailUrl}");
            // 상세에서 못 읽으면 목록에서 본 값을 쓴다.
            return value.HasValue ? value : fromList;
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
    /// 목록의 상세 링크 한 줄에서 아실번호와 동·호를 함께 읽는다.
    /// 매물번호로 지목해 찾으므로 결과가 한 건이고, 그 행의 링크가 곧 상세 주소다.
    /// 링크를 못 찾으면 번호만이라도 다른 방법으로 찾아 본다.
    /// </summary>
    public static (string? AsilNo, DongHo Value) ParseListRow(string html, string articleNo)
    {
        foreach (Match link in AddressLinkPattern.Matches(html ?? string.Empty))
        {
            var asilNo = link.Groups["no"].Value;
            // 검색한 네이버 매물번호가 그대로 링크에 실리는 화면은 상세로 쓸 수 없다.
            if (asilNo.Length == 0 || string.Equals(asilNo, articleNo, StringComparison.Ordinal)) continue;

            // 같은 칸에 메모·버튼 글자가 함께 들어 있어 링크 안쪽만 읽는다.
            var value = DongHoParser.ParseAddress(DongHoParser.Normalize(link.Groups["text"].Value));
            return (asilNo, value);
        }

        var fallback = ParseAsilArticleNo(html ?? string.Empty, articleNo);
        return (fallback, DongHo.Empty);
    }

    /// <summary>
    /// 목록에서 아실 매물번호를 찾는다.
    /// 네이버 매물번호로 검색했으니 결과는 한 건이고, 상세 링크에 번호가 들어 있다.
    /// 검색한 번호 자신은 건너뛴다.
    /// </summary>
    public static string? ParseAsilArticleNo(string html, string articleNo)
    {
        // 목록 행은 상세 링크로 번호를 넘긴다. 이 쪽이 가장 확실하다.
        foreach (Match link in ViewMemulPattern.Matches(html ?? string.Empty))
        {
            var found = link.Groups["value"].Value;
            if (found.Length == 0) continue;
            if (string.Equals(found, articleNo, StringComparison.Ordinal)) continue;
            return found;
        }

        foreach (Match match in AsilNoPattern.Matches(html ?? string.Empty))
        {
            var value = match.Groups["value"].Value;
            if (value.Length == 0) continue;
            if (string.Equals(value, articleNo, StringComparison.Ordinal)) continue;
            return value;
        }
        return null;
    }

    /// <summary>
    /// 목록 행에서 동·호를 읽는다.
    /// 주소 칸이 "단지명 207동 402호 (4층)"처럼 한 덩어리로 들어 있다.
    /// 매물번호로 걸러 조회하므로 결과가 한 건이고, 상세 링크가 있는 칸만 본다.
    /// </summary>
    public static DongHo ParseListDongHo(string html)
    {
        // 같은 칸에 메모·버튼 글자가 함께 들어 있어 링크 안쪽만 읽는다.
        // 메모에 숫자와 '동'·'호'가 들어 있으면 그쪽을 잘못 집을 수 있다.
        foreach (Match link in AddressLinkPattern.Matches(html ?? string.Empty))
        {
            var value = DongHoParser.ParseAddress(DongHoParser.Normalize(link.Groups["text"].Value));
            if (value.HasValue) return value;
        }
        return DongHo.Empty;
    }

    /// <summary>
    /// 상세 화면에서 동·호를 읽는다.
    /// 매물명 칸에 "단지명 104동 1301호" 형태로 함께 들어 있다.
    /// 단지명에는 숫자가 붙은 동 표기가 없어 뒤쪽 동·호만 정확히 걸린다.
    ///
    /// 등록 화면이 대신 열리는 경우를 대비해 입력란도 한 번 더 본다.
    /// 그 화면의 콤보(bld_no)는 건물 일련번호라 동 번호가 아니므로 숨은 입력란을 먼저 쓴다.
    /// </summary>
    public static DongHo ParseDongHo(string html)
    {
        var text = html ?? string.Empty;

        var fromName = DongHoParser.ParseAddress(ReadTableValue(text, "매물명"));
        if (fromName.HasValue) return fromName;

        var dong = ReadInputValue(text, "dong_nm");
        if (dong.Length == 0) dong = ReadSelectedOption(text, "bld_no");
        var ho = ReadInputValue(text, "adr_ho");
        return new DongHo(NormalizeDong(dong), NormalizeHo(ho));
    }

    /// <summary>상세 표에서 항목 이름(예: 매물명) 바로 뒤에 오는 값을 읽는다.</summary>
    private static string ReadTableValue(string html, string header)
    {
        var match = Regex.Match(
            html,
            $@"<th[^>]*>\s*{Regex.Escape(header)}\s*</th>\s*<td[^>]*>(?<value>.*?)</td>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? DongHoParser.Normalize(match.Groups["value"].Value) : string.Empty;
    }

    /// <summary>이름이 name인 콤보에서 선택된 항목의 값을 읽는다.</summary>
    private static string ReadSelectedOption(string html, string name)
    {
        var select = Regex.Match(
            html,
            $@"<select[^>]*\bname\s*=\s*[""']{Regex.Escape(name)}[""'][^>]*>(?<body>.*?)</select>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!select.Success) return string.Empty;

        var option = SelectedOptionPattern.Match(select.Groups["body"].Value);
        return option.Success ? DongHoParser.Normalize(option.Groups["text"].Value) : string.Empty;
    }

    /// <summary>이름이 name인 입력란의 값을 읽는다.</summary>
    private static string ReadInputValue(string html, string name)
    {
        var input = Regex.Match(
            html,
            $@"<input[^>]*\bname\s*=\s*[""']{Regex.Escape(name)}[""'][^>]*>",
            RegexOptions.IgnoreCase);
        if (!input.Success) return string.Empty;

        var value = Regex.Match(input.Value, @"\bvalue\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase);
        return value.Success ? WebUtility.HtmlDecode(value.Groups["value"].Value).Trim() : string.Empty;
    }

    /// <summary>'103'처럼 숫자만 오면 '103동' 형태로 맞춘다.</summary>
    private static string NormalizeDong(string value) => AppendSuffix(value, '동');

    /// <summary>'702'처럼 숫자만 오면 '702호' 형태로 맞춘다.</summary>
    private static string NormalizeHo(string value) => AppendSuffix(value, '호');

    private static string AppendSuffix(string value, char suffix)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        if (text[^1] == suffix) return text;
        return DigitPattern.IsMatch(text) ? text + suffix : string.Empty;
    }

    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        var body = Decode(bytes, charset);

        if (IsLoggedOutPage(body))
        {
            _loggedIn = false;
            CpLoginTrace.Write(
                $"아실 화면 열기 실패(로그인 안 됨) · 길이 {body.Length} · {url}" +
                $" · 내용 {Preview(body)}");
            return null;
        }
        return body;
    }

    /// <summary>
    /// 로그인이 풀렸을 때 돌아오는 화면인지 본다.
    /// 안내창을 띄우고 곧바로 옮겨 가는 아주 짧은 화면이라 길이로도 구분된다.
    /// 정상 매물 화면에도 로그아웃 링크 때문에 login.jsp 글자가 들어 있어,
    /// 그 글자만으로 판단하면 멀쩡한 화면을 로그아웃으로 잘못 본다.
    /// </summary>
    private static bool IsLoggedOutPage(string body)
    {
        if (body.Contains("로그인후 이용하세요", StringComparison.Ordinal)) return true;
        return body.Length < 2000 &&
               body.Contains("location.href", StringComparison.OrdinalIgnoreCase) &&
               body.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch (ArgumentException) { /* 모르는 이름이면 아래로 넘어간다. */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginGate.Dispose();
        _client.Dispose();
    }
}
