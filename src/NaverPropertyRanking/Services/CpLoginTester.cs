using System.Net;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services.Security;

namespace NaverPropertyRanking.Services;

/// <summary>CP 사이트 접속 테스트 결과.</summary>
public sealed record CpLoginTestResult(bool Success, string Message)
{
    public static CpLoginTestResult Ok(string message) => new(true, message);
    public static CpLoginTestResult Fail(string message) => new(false, message);
}

/// <summary>
/// 브라우저 창을 띄우지 않고 CP 사이트 로그인을 내부에서 시도해 접속 가능 여부만 확인한다.
/// 로그인 페이지의 폼을 그대로 읽어 숨은 값(CSRF·VIEWSTATE 등)까지 함께 보내므로
/// 사이트마다 입력란 이름이 달라도 대부분 동작한다.
/// </summary>
public static class CpLoginTester
{
    // 이실장 로그인에 쓰는 고정 값과 주소. 사이트 로그인 화면이 보내는 값과 같다.
    private const string AipartnerAgentId = "100";
    private const string AipartnerServiceCode = "1000";
    private const string AipartnerLoginCode = "1";
    private const string AipartnerSuccessCode = "000000";
    private const string AipartnerPublicKeyUrl =
        "https://sso.aipartner.com/openapi/authentication/publickey/get";
    private const string AipartnerLoginProcessUrl =
        "https://sso.aipartner.com/authentication/issacweb/loginProcess";
    private const string AipartnerLoginInfoSaveUrl =
        "https://www.aipartner.com/api/web/integrated/login-info-save";
    private const string AipartnerRequestPage = "https://www.aipartner.com/home";
    /// <summary>로그인 확정 폼을 따라가는 최대 횟수. 같은 화면을 계속 되받는 상황을 막는다.</summary>
    private const int MaxAipartnerHandoffHops = 5;

    private static readonly Regex FormPattern = new(
        @"<form\b[^>]*>.*?</form>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InputPattern = new(
        @"<input\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttributePattern = new(
        @"(?<name>[\w:-]+)\s*=\s*(""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<CpLoginTestResult> TestAsync(
        CpSite site,
        CpAccount account,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(account.UserId) || string.IsNullOrEmpty(account.Password))
            return CpLoginTestResult.Fail("아이디와 패스워드를 먼저 저장해 주세요.");
        if (!site.CanTestLogin)
            return CpLoginTestResult.Fail(
                $"{site.Name}은(는) 접속 주소가 아직 등록되지 않아 접속 테스트를 할 수 없습니다. 계정 저장은 됩니다.");

        var cookies = new CookieContainer();
        handler ??= new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = true,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
        return await TestWithClientAsync(client, site, account, cancellationToken);
    }

    /// <summary>
    /// 이미 만들어 둔 HttpClient로 로그인한다.
    /// 로그인 뒤 같은 세션으로 다른 페이지를 계속 조회할 때 쓴다.
    /// </summary>
    public static async Task<CpLoginTestResult> TestWithClientAsync(
        HttpClient client,
        CpSite site,
        CpAccount account,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.UserId) || string.IsNullOrEmpty(account.Password))
            return CpLoginTestResult.Fail("아이디와 패스워드를 먼저 저장해 주세요.");

        try
        {
            // CP마다 로그인 방식이 달라 사이트별 처리를 먼저 태운다.
            // 여기서 걸리지 않는 CP만 아래의 일반 폼 전송 방식을 쓴다.
            switch (site.Value)
            {
                case "1": return await TestRfineAsync(client, site, account, cancellationToken);
                case "2": return await TestNeonetAsync(client, site, account, cancellationToken);
                case "3": return await TestAipartnerAsync(client, site, account, cancellationToken);
            }

            using var pageResponse = await client.GetAsync(site.LoginUrl, cancellationToken);
            if (!pageResponse.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 페이지를 열지 못했습니다(HTTP {(int)pageResponse.StatusCode}).");

            var loginPageUrl = pageResponse.RequestMessage?.RequestUri ?? new Uri(site.LoginUrl);
            var html = await ReadBodyAsync(pageResponse, cancellationToken);
            var form = FindLoginForm(html);
            if (form is null)
                return CpLoginTestResult.Fail("로그인 폼을 찾지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

            var fields = BuildFormFields(form, account.UserId, account.Password);
            if (fields is null)
                return CpLoginTestResult.Fail("아이디 입력란을 찾지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

            var actionUrl = ResolveAction(form, loginPageUrl);
            using var content = new FormUrlEncodedContent(fields);
            using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
            request.Headers.Referrer = loginPageUrl;
            using var loginResponse = await client.SendAsync(request, cancellationToken);

            if (!loginResponse.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

            var resultHtml = await ReadBodyAsync(loginResponse, cancellationToken);
            var finalUrl = loginResponse.RequestMessage?.RequestUri ?? actionUrl;
            return Evaluate(resultHtml, finalUrl, actionUrl, loginPageUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CpLoginTestResult.Fail("응답 시간이 20초를 넘었습니다. 네트워크 상태를 확인해 주세요.");
        }
        catch (HttpRequestException ex)
        {
            return CpLoginTestResult.Fail($"사이트에 연결할 수 없습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// 부동산포스 전용 로그인.
    /// 로그인 페이지에서 csrf_token을 받아 /process/member.action.php에 POST하고,
    /// 응답 JSON의 resultCode가 Y인지로 성공을 판정한다(사이트 스크립트와 같은 방식).
    /// </summary>
    private static async Task<CpLoginTestResult> TestRfineAsync(
        HttpClient client,
        CpSite site,
        CpAccount account,
        CancellationToken cancellationToken)
    {
        using var pageResponse = await client.GetAsync(site.LoginUrl, cancellationToken);
        if (!pageResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 페이지를 열지 못했습니다(HTTP {(int)pageResponse.StatusCode}).");

        var loginPageUrl = pageResponse.RequestMessage?.RequestUri ?? new Uri(site.LoginUrl);
        var html = await ReadBodyAsync(pageResponse, cancellationToken);
        var csrfToken = ReadCsrfToken(html);
        if (csrfToken is null)
            return CpLoginTestResult.Fail("보안 토큰(csrf_token)을 찾지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

        var actionUrl = new Uri(loginPageUrl, "/process/member.action.php");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "loginCheck2",
            ["ID"] = account.UserId,
            ["Passwd"] = account.Password,
            ["isLoginID"] = string.Empty,
            ["csrf_token"] = csrfToken
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
        request.Headers.Referrer = loginPageUrl;
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var loginResponse = await client.SendAsync(request, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

        var body = await ReadBodyAsync(loginResponse, cancellationToken);
        return ReadRfineResult(body);
    }

    /// <summary>
    /// 부동산뱅크 전용 로그인.
    /// 로그인 폼의 action이 자바스크립트로 정해지므로 그 주소로 바로 POST한다.
    /// 응답은 본문 없이 location.href 한 줄로 다음 화면을 지정하는데,
    /// 실패하면 login_check=no를 달고 로그인 페이지로 되돌린다.
    /// </summary>
    private static async Task<CpLoginTestResult> TestNeonetAsync(
        HttpClient client,
        CpSite site,
        CpAccount account,
        CancellationToken cancellationToken)
    {
        using var pageResponse = await client.GetAsync(site.LoginUrl, cancellationToken);
        if (!pageResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 페이지를 열지 못했습니다(HTTP {(int)pageResponse.StatusCode}).");

        var loginPageUrl = pageResponse.RequestMessage?.RequestUri ?? new Uri(site.LoginUrl);
        var actionUrl = new Uri(loginPageUrl, "/novo-rebank/view/login/ptl.login_after.usr.neo");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["return_url"] = "/novo-rebank/index.neo",
            ["pop"] = string.Empty,
            ["cyber"] = string.Empty,
            ["homepage"] = string.Empty,
            ["ssl"] = "ok",
            ["id"] = account.UserId,
            ["pw"] = account.Password
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
        request.Headers.Referrer = loginPageUrl;
        using var loginResponse = await client.SendAsync(request, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

        var body = await ReadBodyAsync(loginResponse, cancellationToken);
        return ReadNeonetResult(body);
    }

    /// <summary>부동산뱅크 응답의 이동 주소로 성공 여부를 판단한다.</summary>
    public static CpLoginTestResult ReadNeonetResult(string body)
    {
        var destination = Regex.Match(
            body,
            @"location\.href\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase);

        // 이동 주소가 없으면 판단 근거가 없으므로 성공으로 넘기지 않는다.
        if (!destination.Success)
            return body.Contains("MemberLogin", StringComparison.OrdinalIgnoreCase)
                ? CpLoginTestResult.Fail("아이디 또는 비밀번호가 올바르지 않습니다.")
                : CpLoginTestResult.Fail("로그인 결과를 확인하지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

        var url = WebUtility.HtmlDecode(destination.Groups["url"].Value);
        if (url.Contains("login_check=no", StringComparison.OrdinalIgnoreCase))
            return CpLoginTestResult.Fail("아이디 또는 비밀번호가 올바르지 않습니다.");
        if (url.Contains("MemberLogin", StringComparison.OrdinalIgnoreCase))
            return CpLoginTestResult.Fail("로그인 페이지로 되돌아왔습니다. 계정 정보를 확인해 주세요.");
        return CpLoginTestResult.Ok("로그인에 성공했습니다.");
    }

    /// <summary>
    /// 이실장 전용 로그인.
    /// 사이트가 아이디 형태에 따라 두 갈래로 나뉘므로 같은 갈래를 그대로 따라간다.
    ///  - 휴대폰번호 아이디(기존 회원): loginStore에 그대로 보낸다.
    ///  - 그 밖의 아이디: SSO 쪽에 ISSAC WebCrypto로 암호화해 보낸다.
    /// </summary>
    private static async Task<CpLoginTestResult> TestAipartnerAsync(
        HttpClient client,
        CpSite site,
        CpAccount account,
        CancellationToken cancellationToken)
    {
        using var pageResponse = await client.GetAsync(site.LoginUrl, cancellationToken);
        if (!pageResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 페이지를 열지 못했습니다(HTTP {(int)pageResponse.StatusCode}).");

        var loginPageUrl = pageResponse.RequestMessage?.RequestUri ?? new Uri(site.LoginUrl);
        var html = await ReadBodyAsync(pageResponse, cancellationToken);

        var csrfToken = ReadMetaCsrfToken(html);
        CpLoginTrace.Write($"이실장 로그인 화면 열림 · 주소 {loginPageUrl} · 보안토큰 {(csrfToken is null ? "없음" : "있음")}");
        if (csrfToken is null)
            return CpLoginTestResult.Fail("로그인 화면에서 보안 토큰을 찾지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

        CpLoginTrace.Write($"경로 선택 · {(IsPhoneNumberId(account.UserId) ? "기존 회원(loginStore)" : "SSO 암호화")}");
        return IsPhoneNumberId(account.UserId)
            ? await TestAipartnerLegacyAsync(client, account, loginPageUrl, csrfToken, cancellationToken)
            : await TestAipartnerSsoAsync(client, account, loginPageUrl, csrfToken, cancellationToken);
    }

    /// <summary>휴대폰번호 아이디를 쓰는 기존 회원용 경로.</summary>
    private static async Task<CpLoginTestResult> TestAipartnerLegacyAsync(
        HttpClient client,
        CpAccount account,
        Uri loginPageUrl,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        var actionUrl = new Uri(loginPageUrl, "/api/web/integrated/loginStore");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = AipartnerAgentId,
            ["issacwebData"] = string.Empty,
            ["serviceCode"] = AipartnerServiceCode,
            ["loginCode"] = AipartnerLoginCode,
            ["requestPage"] = "https://www.aipartner.com/home",
            ["member-id"] = account.UserId,
            ["member-pw"] = account.Password
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
        request.Headers.Referrer = loginPageUrl;
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        using var loginResponse = await client.SendAsync(request, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

        var body = await ReadBodyAsync(loginResponse, cancellationToken);
        CpLoginTrace.Write($"loginStore 응답 · 최종주소 {loginResponse.RequestMessage?.RequestUri}");
        var legacyResult = ReadAipartnerResult(body);
        CpLoginTrace.Write($"loginStore 판정 · 성공 {legacyResult.Success} · {legacyResult.Message}");
        return legacyResult;
    }

    /// <summary>
    /// 일반 아이디를 쓰는 SSO 경로.
    /// 공개키와 타임스탬프를 받아 아이디·비밀번호를 암호화한 뒤 loginProcess에 보낸다.
    /// 비밀번호는 이 과정에서만 평문으로 존재하고 요청에는 암호문만 실린다.
    /// </summary>
    private static async Task<CpLoginTestResult> TestAipartnerSsoAsync(
        HttpClient client,
        CpAccount account,
        Uri loginPageUrl,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        using var keyResponse = await client.GetAsync(AipartnerPublicKeyUrl, cancellationToken);
        if (!keyResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"공개키를 받지 못했습니다(HTTP {(int)keyResponse.StatusCode}).");

        var keyBody = await ReadBodyAsync(keyResponse, cancellationToken);
        var key = ReadAipartnerPublicKey(keyBody);
        CpLoginTrace.Write($"공개키 · {(key is null ? "읽기 실패" : "확보")}");
        if (key is null)
            return CpLoginTestResult.Fail("공개키를 읽지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");

        string issacwebData;
        try
        {
            var message = IssacWebCrypto.BuildLoginMessage(account.UserId, account.Password, key.Value.TimeStamp);
            issacwebData = IssacWebCrypto.HybridEncrypt(message, key.Value.PublicKey);
        }
        catch (Exception ex)
        {
            return CpLoginTestResult.Fail($"로그인 정보를 암호화하지 못했습니다: {ex.Message}");
        }

        // 인증 결과를 받을 준비를 이실장 쪽에 먼저 시켜야 한다.
        // 이 요청이 빠지면 SSO 인증은 성공해도 이실장에는 로그인이 붙지 않는다.
        var prepared = await SaveAipartnerLoginInfoAsync(
            client, account, loginPageUrl, csrfToken, cancellationToken);
        if (!prepared.Success) return prepared;

        var actionUrl = new Uri(AipartnerLoginProcessUrl);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = AipartnerAgentId,
            ["loginCode"] = AipartnerLoginCode,
            ["issacwebData"] = issacwebData,
            ["serviceCode"] = AipartnerServiceCode
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = content };
        request.Headers.Referrer = loginPageUrl;
        using var loginResponse = await client.SendAsync(request, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 요청이 거부되었습니다(HTTP {(int)loginResponse.StatusCode}).");

        var body = await ReadBodyAsync(loginResponse, cancellationToken);
        var verdict = ReadAipartnerSsoResult(body);
        CpLoginTrace.Write($"loginProcess 판정 · 성공 {verdict.Success} · {verdict.Message}");
        if (!verdict.Success) return verdict;

        // 여기까지는 인증만 끝난 상태다. 화면은 응답으로 받은 폼을 한 번 더 보내고,
        // 그 요청이 도착해야 이실장 쪽에 로그인 세션이 생긴다. 그래서 같은 전송을 이어서 한다.
        var pageUrl = loginResponse.RequestMessage?.RequestUri ?? actionUrl;
        return await CompleteAipartnerSsoAsync(client, pageUrl, body, cancellationToken);
    }

    /// <summary>
    /// 로그인 화면이 SSO로 넘어가기 직전에 보내는 요청.
    /// 입력값과 돌아올 주소를 이실장 세션에 저장해 두어, SSO 인증이 끝나면
    /// 그 세션에 로그인이 붙는다. 사이트가 보내는 것과 같은 모양으로 보낸다.
    /// </summary>
    private static async Task<CpLoginTestResult> SaveAipartnerLoginInfoAsync(
        HttpClient client,
        CpAccount account,
        Uri loginPageUrl,
        string csrfToken,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string>
        {
            ["requestPage"] = AipartnerRequestPage,
            ["serviceCode"] = AipartnerServiceCode,
            ["formData[agentId]"] = AipartnerAgentId,
            ["formData[issacwebData]"] = string.Empty,
            ["formData[serviceCode]"] = AipartnerServiceCode,
            ["formData[loginCode]"] = AipartnerLoginCode,
            ["formData[requestPage]"] = AipartnerRequestPage,
            ["formData[member-id]"] = account.UserId,
            ["formData[member-pw]"] = account.Password
        };

        using var content = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, AipartnerLoginInfoSaveUrl)
        {
            Content = content
        };
        request.Headers.Referrer = loginPageUrl;
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return CpLoginTestResult.Fail($"로그인 준비 요청이 거부되었습니다(HTTP {(int)response.StatusCode}).");

        var body = await ReadBodyAsync(response, cancellationToken);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var accepted = document.RootElement.TryGetProperty("rs", out var rs) &&
                           rs.ValueKind == System.Text.Json.JsonValueKind.True;
            CpLoginTrace.Write($"login-info-save · 수락 {accepted}");
            return accepted
                ? CpLoginTestResult.Ok("로그인 준비를 마쳤습니다.")
                : CpLoginTestResult.Fail("로그인 준비 단계에서 거절되었습니다. 잠시 후 다시 시도해 주세요.");
        }
        catch (System.Text.Json.JsonException)
        {
            return CpLoginTestResult.Fail("로그인 준비 응답을 해석하지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");
        }
    }

    /// <summary>
    /// SSO가 돌려준 폼을 그대로 다시 보내 로그인을 확정한다.
    /// 이 단계를 건너뛰면 로그인은 성공했는데 매물 화면은 열리지 않는다.
    /// </summary>
    private static async Task<CpLoginTestResult> CompleteAipartnerSsoAsync(
        HttpClient client,
        Uri pageUrl,
        string html,
        CancellationToken cancellationToken)
    {
        // 인증이 끝난 뒤에도 화면이 폼을 몇 번 더 자동 전송해야 로그인이 확정된다.
        // 사이트가 하는 것과 같은 순서로 따라가되, 무한히 돌지 않도록 횟수를 막는다.
        var currentUrl = pageUrl;
        var currentHtml = html;
        var submitted = false;

        for (var hop = 0; hop < MaxAipartnerHandoffHops; hop++)
        {
            var form = SelectHandoffForm(currentHtml);
            if (form is null)
            {
                // 폼이 없으면 주소로 옮겨 가는 단계다.
                var next = ReadAipartnerNextUrl(currentHtml, currentUrl);
                if (next is null || next == currentUrl)
                {
                    CpLoginTrace.Write($"이어갈 단계 없음 · {hop + 1}회차에서 종료");
                    break;
                }

                using var moveResponse = await client.GetAsync(next, cancellationToken);
                currentUrl = moveResponse.RequestMessage?.RequestUri ?? next;
                currentHtml = await ReadBodyAsync(moveResponse, cancellationToken);
                CpLoginTrace.Write($"이동 {hop + 1}회 · 요청 {next} · 도착 {currentUrl}");
                TraceResponseShape(currentHtml, moveResponse);

                var moveAbort = DetectAipartnerAbort(currentUrl);
                if (moveAbort is not null) return moveAbort;
                continue;
            }

            var action = ReadAttributes(form.OpenTag).GetValueOrDefault("action", string.Empty);

            // action이 비어 있으면 스크립트가 전송 직전에 넣어 준다. 그 주소를 화면에서 찾는다.
            var target = string.IsNullOrWhiteSpace(action)
                ? FindScriptAction(currentHtml, currentUrl)
                : new Uri(currentUrl, action);
            if (target is null)
            {
                CpLoginTrace.Write("보낼 주소를 찾지 못해 중단");
                break;
            }
            // 같은 주소로 되쏘면 사이트가 오류로 보고 로그아웃시킨다.
            if (target == currentUrl)
            {
                CpLoginTrace.Write($"같은 주소로 되쏘게 되어 중단 · {target}");
                break;
            }

            // 폼 태그 안에 값이 없으면 화면에 흩어져 있는 숨은 값을 대신 담는다.
            // 닫는 태그가 어긋난 화면에서는 값이 폼 밖으로 밀려나 있다.
            var source = InputPattern.Matches(form.Content).Count > 0 ? form.Content : currentHtml;
            var fields = new Dictionary<string, string>();
            foreach (Match input in InputPattern.Matches(source))
            {
                var attributes = ReadAttributes(input.Value);
                var name = attributes.GetValueOrDefault("name", string.Empty);
                if (name.Length == 0) continue;
                fields[name] = WebUtility.HtmlDecode(attributes.GetValueOrDefault("value", string.Empty));
            }

            // 응답이 늦을 수 있어 보내기 전에도 남긴다. 어디서 멈춰 있는지 알 수 있게 하기 위해서다.
            CpLoginTrace.Write($"확정 폼 {hop + 1}회 보내는 중 · 보낼곳 {target} · 항목 [{string.Join(", ", fields.Keys)}]");

            using var content = new FormUrlEncodedContent(fields);
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
            request.Headers.Referrer = currentUrl;
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return CpLoginTestResult.Fail($"로그인 확정 요청이 거부되었습니다(HTTP {(int)response.StatusCode}).");

            submitted = true;
            currentUrl = response.RequestMessage?.RequestUri ?? target;
            currentHtml = await ReadBodyAsync(response, cancellationToken);
            CpLoginTrace.Write(
                $"확정 폼 {hop + 1}회 · 보낸곳 {target} · HTTP {(int)response.StatusCode} · 도착 {currentUrl}");
            TraceResponseShape(currentHtml, response);

            var abort = DetectAipartnerAbort(currentUrl);
            if (abort is not null) return abort;
        }

        if (!submitted)
        {
            CpLoginTrace.Write("확정 폼 없음");
            return CpLoginTestResult.Fail("로그인 확정 단계를 찾지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");
        }

        return CpLoginTestResult.Ok("로그인에 성공했습니다.");
    }

    /// <summary>이어서 보낼 폼 하나. 여는 태그와 그 안의 내용으로만 다룬다.</summary>
    private sealed record HandoffForm(string OpenTag, string Content);

    /// <summary>
    /// 이어서 보낼 폼을 고른다.
    /// 화면이 여러 폼을 담고 있을 때는 보낼 곳이 적힌 폼이 실제로 전송되는 폼이다.
    /// 닫는 태그가 빠진 화면이 있어 여는 태그를 기준으로 잘라 쓴다.
    /// </summary>
    private static HandoffForm? SelectHandoffForm(string html)
    {
        var opens = Regex.Matches(html, "<form[^>]*>", RegexOptions.IgnoreCase);
        if (opens.Count == 0) return null;

        var forms = new List<HandoffForm>();
        for (var index = 0; index < opens.Count; index++)
        {
            var start = opens[index].Index + opens[index].Length;
            var end = html.Length;

            var close = html.IndexOf("</form", start, StringComparison.OrdinalIgnoreCase);
            if (close >= 0) end = close;
            // 닫는 태그가 없어도 다음 폼이 시작되면 거기까지가 이 폼이다.
            if (index + 1 < opens.Count) end = Math.Min(end, opens[index + 1].Index);

            forms.Add(new HandoffForm(opens[index].Value, html[start..end]));
        }

        static bool HasAction(HandoffForm candidate) =>
            !string.IsNullOrWhiteSpace(ReadAttributes(candidate.OpenTag).GetValueOrDefault("action", string.Empty));

        // 단계를 이어 주는 폼은 숨은 값만 담고 있다.
        // 검색창처럼 사람이 입력하는 폼을 실수로 보내지 않도록 구분한다.
        static bool IsRelayForm(HandoffForm candidate)
        {
            var inputs = InputPattern.Matches(candidate.Content);
            if (inputs.Count == 0) return false;
            return inputs.All(input =>
                string.Equals(ReadAttributes(input.Value).GetValueOrDefault("type", string.Empty),
                    "hidden", StringComparison.OrdinalIgnoreCase));
        }

        static HandoffForm Chosen(HandoffForm candidate)
        {
            CpLoginTrace.Write(
                $"고른 폼 · action [{ReadAttributes(candidate.OpenTag).GetValueOrDefault("action", "(비어 있음)")}]");
            return candidate;
        }

        var named = forms.FirstOrDefault(form =>
            Regex.IsMatch(form.OpenTag, "id\\s*=\\s*[\"']form-send[\"']", RegexOptions.IgnoreCase));

        // 보낼 곳이 적힌 폼이 실제로 전송되는 폼이다.
        // 이름이 붙은 폼이라도 action이 비어 있으면 그것만 믿지 않는다.
        if (named is not null && HasAction(named)) return Chosen(named);

        var withAction = forms.FirstOrDefault(HasAction);
        if (withAction is not null) return Chosen(withAction);

        if (named is not null) return Chosen(named);

        var relay = forms.FirstOrDefault(IsRelayForm);
        return relay is null ? null : Chosen(relay);
    }

    /// <summary>로그인 흐름이 오류·로그아웃·로그인 화면으로 튕겼는지 본다.</summary>
    private static CpLoginTestResult? DetectAipartnerAbort(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.Contains("/integrated/login", StringComparison.OrdinalIgnoreCase))
            return CpLoginTestResult.Fail("로그인 화면으로 되돌아왔습니다. 계정 정보를 확인해 주세요.");
        if (path.Contains("/sso/error", StringComparison.OrdinalIgnoreCase))
            return CpLoginTestResult.Fail("사이트가 로그인 절차를 거절했습니다(sso/error).");
        if (path.Contains("logout", StringComparison.OrdinalIgnoreCase))
            return CpLoginTestResult.Fail("로그인 도중 로그아웃 처리되었습니다.");
        return null;
    }

    /// <summary>
    /// 폼의 action이 비어 있을 때 스크립트가 넣어 주는 주소를 찾는다.
    /// 먼저 action 대입 구문을 보고, 없으면 화면에 있는 주소 중 정적 파일이 아닌 것을 쓴다.
    /// </summary>
    private static Uri? FindScriptAction(string html, Uri baseUrl)
    {
        // 1) action을 직접 넣어 주는 구문이 있으면 그것이 가장 확실하다.
        var assigned = Regex.Match(
            html,
            @"(?:attr\(\s*['""]action['""]\s*,\s*|\.action\s*=\s*)['""](?<url>[^'""]+)['""]",
            RegexOptions.IgnoreCase);
        if (assigned.Success && Uri.TryCreate(baseUrl, assigned.Groups["url"].Value, out var direct))
            return direct;

        // 2) 보낼 주소는 스크립트 안에 적혀 있다.
        //    본문의 링크(고객센터 등)를 집지 않도록 스크립트 안만 본다.
        var scriptUrls = Regex.Matches(html, @"<script\b[^>]*>(?<code>.*?)</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .SelectMany(script => UrlLiterals(script.Groups["code"].Value))
            .ToList();
        CpLoginTrace.Write($"스크립트 주소후보 [{string.Join(" | ", scriptUrls.Take(10))}]");

        var picked = scriptUrls.LastOrDefault(url =>
                Regex.IsMatch(url, "token|auth|login|sso|process|check", RegexOptions.IgnoreCase))
            ?? scriptUrls.LastOrDefault();
        if (picked is not null && Uri.TryCreate(baseUrl, picked, out var fromScript))
            return fromScript;

        return null;
    }

    /// <summary>따옴표로 묶인 주소 중 화면을 꾸미는 파일이 아닌 것만 고른다.</summary>
    private static IEnumerable<string> UrlLiterals(string text) =>
        Regex.Matches(text, @"['""](?<url>(?:https?://|/)[A-Za-z0-9_\-./?=&]{3,})['""]")
            .Select(match => match.Groups["url"].Value)
            .Where(url => !IsStaticAsset(url));


    /// <summary>화면을 꾸미는 파일인지. 이런 주소로는 폼을 보내지 않는다.</summary>
    private static bool IsStaticAsset(string url) =>
        Regex.IsMatch(url, @"\.(css|js|ico|png|jpe?g|gif|svg|webp|woff2?|ttf|map)(\?|$)", RegexOptions.IgnoreCase) ||
        url.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/vendor/", StringComparison.OrdinalIgnoreCase);


    /// <summary>응답이 무엇으로 시작하는지만 아주 짧게 남긴다(형태 판별용).</summary>
    private static string Preview(string body)
    {
        var text = (body ?? string.Empty).TrimStart();
        if (text.Length == 0) return "(빈 응답)";
        var head = text[..Math.Min(12, text.Length)];
        return new string(head.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    }

    /// <summary>
    /// 응답이 어떤 모양인지만 기록한다.
    /// 토큰이 새지 않도록 주소와 입력란 '이름'만 남기고 값은 절대 남기지 않는다.
    /// </summary>
    private static void TraceResponseShape(string body, HttpResponseMessage response)
    {
        if (!CpLoginTrace.Enabled) return;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "(없음)";
        var forms = Regex.Matches(body, "<form[^>]*>", RegexOptions.IgnoreCase)
            .Select(match => ReadAttributes(match.Value).GetValueOrDefault("action", "(빈 action)"))
            .ToList();
        var inputNames = InputPattern.Matches(body)
            .Select(match => ReadAttributes(match.Value).GetValueOrDefault("name", string.Empty))
            .Where(name => name.Length > 0)
            .Distinct()
            .ToList();
        var moves = Regex.Matches(body, @"(location\.\w+|\.submit\(\)|window\.open|meta[^>]*refresh)",
                RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .Distinct()
            .Take(6)
            .ToList();

        CpLoginTrace.Write(
            $"확정 응답 모양 · 형식 {contentType} · 길이 {body.Length}" +
            $" · 시작 {Preview(body)}" +
            $" · 폼 [{string.Join(" | ", forms)}]" +
            $" · 입력이름 [{string.Join(", ", inputNames)}]" +
            $" · 이동표현 [{string.Join(", ", moves)}]");

        var urls = Regex.Matches(body, @"['""](?<url>(?:https?://|/)[A-Za-z0-9_\-./?=&]{3,})['""]")
            .Select(match => match.Groups["url"].Value)
            .Distinct()
            .Take(12)
            .ToList();
        var resultCode = Regex.Match(
            body,
            @"name\s*=\s*['""]resultCode['""][^>]*value\s*=\s*['""](?<code>[^'""]*)['""]",
            RegexOptions.IgnoreCase);
        CpLoginTrace.Write(
            $"확정 응답 주소후보 [{string.Join(" | ", urls)}]" +
            $" · resultCode {(resultCode.Success ? resultCode.Groups["code"].Value : "(없음)")}");
    }

    /// <summary>
    /// 로그인 확정 응답에서 다음으로 옮겨 갈 주소를 찾는다.
    /// JSON으로 오는 경우와 자바스크립트 이동으로 오는 경우를 모두 본다.
    /// 기록에는 주소만 남기고 토큰 같은 값은 남기지 않는다.
    /// </summary>
    private static Uri? ReadAipartnerNextUrl(string body, Uri baseUrl)
    {
        var text = (body ?? string.Empty).Trim('﻿').TrimStart();

        if (text.StartsWith('{'))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(text);
                var root = document.RootElement;
                CpLoginTrace.Write(
                    $"확정 응답(JSON) 항목 · {string.Join(", ", root.EnumerateObject().Select(item => item.Name))}");

                foreach (var name in new[] { "returnUri", "returnUrl", "redirectUrl", "url" })
                {
                    if (root.TryGetProperty(name, out var element) &&
                        element.ValueKind == System.Text.Json.JsonValueKind.String &&
                        Uri.TryCreate(baseUrl, element.GetString(), out var parsed))
                        return parsed;
                }
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        var move = Regex.Match(
            text,
            @"location(?:\.href|\.replace\()\s*=?\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase);
        return move.Success && Uri.TryCreate(baseUrl, WebUtility.HtmlDecode(move.Groups["url"].Value), out var target)
            ? target
            : null;
    }

    /// <summary>공개키 응답에서 공개키와 타임스탬프를 읽는다.</summary>
    public static (string PublicKey, string TimeStamp)? ReadAipartnerPublicKey(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("resultCode", out var code) || code.GetString() != AipartnerSuccessCode)
                return null;
            if (!root.TryGetProperty("resultData", out var data)) return null;

            var publicKey = data.TryGetProperty("publicKey", out var keyElement) ? keyElement.GetString() : null;
            var timeStamp = data.TryGetProperty("timeStamp", out var stampElement) ? stampElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(timeStamp)) return null;
            return (publicKey, timeStamp);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// SSO 로그인 응답을 읽는다.
    /// 응답은 결과를 숨은 입력값으로 담아 되돌리는 폼이라 resultCode로 판정한다.
    /// </summary>
    public static CpLoginTestResult ReadAipartnerSsoResult(string body)
    {
        var code = ReadHiddenInput(body, "resultCode");
        var message = ReadHiddenInput(body, "resultMessage");

        if (code is null)
            return CpLoginTestResult.Fail("로그인 결과를 확인하지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");
        if (code == AipartnerSuccessCode)
            return CpLoginTestResult.Ok("로그인에 성공했습니다.");
        return CpLoginTestResult.Fail(
            string.IsNullOrWhiteSpace(message) ? $"로그인에 실패했습니다(코드 {code})." : message);
    }

    /// <summary>이실장 loginStore 응답 JSON에서 성공 여부와 안내 문구를 읽는다.</summary>
    public static CpLoginTestResult ReadAipartnerResult(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            var success = root.TryGetProperty("result", out var result) &&
                          result.ValueKind == System.Text.Json.JsonValueKind.True;
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            if (success)
                return CpLoginTestResult.Ok(message.Length > 0 ? message : "로그인에 성공했습니다.");
            return CpLoginTestResult.Fail(message.Length > 0 ? message : "로그인에 실패했습니다.");
        }
        catch (System.Text.Json.JsonException)
        {
            return CpLoginTestResult.Fail("로그인 응답을 해석하지 못했습니다. 사이트 구조가 바뀌었을 수 있습니다.");
        }
    }

    /// <summary>이실장이 기존 회원 경로로 받아 주는 휴대폰번호 형식인지 확인한다.</summary>
    public static bool IsPhoneNumberId(string? userId) =>
        Regex.IsMatch(userId ?? string.Empty, @"^01[0-9]-?[0-9]{3,4}-?[0-9]{4}$");

    /// <summary>이름이 name인 숨은 입력값을 읽는다.</summary>
    private static string? ReadHiddenInput(string html, string name)
    {
        var match = Regex.Match(
            html,
            @"<input[^>]*\bname\s*=\s*[""']" + Regex.Escape(name) + @"[""'][^>]*>",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = ReadAttributes(match.Value).GetValueOrDefault("value", string.Empty);
        return WebUtility.HtmlDecode(value);
    }

    /// <summary>meta 태그(name="csrf-token")에 담긴 토큰을 읽는다.</summary>
    private static string? ReadMetaCsrfToken(string html)
    {
        var match = Regex.Match(
            html,
            @"<meta[^>]*name\s*=\s*[""']csrf-token[""'][^>]*>",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = ReadAttributes(match.Value).GetValueOrDefault("content", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value);
    }

    /// <summary>
    /// 응답 본문을 문자열로 읽는다.
    /// EUC-KR처럼 .NET이 기본으로 모르는 인코딩을 쓰는 사이트가 있어 그대로 읽으면 예외가 난다.
    /// 그럴 때는 바이트를 Latin-1로 옮긴다. 판정에 쓰는 주소·코드는 모두 ASCII라 영향이 없다.
    /// </summary>
    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }

    private static string? ReadCsrfToken(string html)
    {
        var match = Regex.Match(
            html,
            @"<input[^>]*id\s*=\s*[""']csrf_token[""'][^>]*>",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = ReadAttributes(match.Value).GetValueOrDefault("value", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value);
    }

    /// <summary>사이트가 돌려주는 JSON에서 성공 여부와 안내 문구를 읽는다.</summary>
    private static CpLoginTestResult ReadRfineResult(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = root.TryGetProperty("resultCode", out var codeElement)
                ? codeElement.GetString() ?? string.Empty
                : string.Empty;
            var message = root.TryGetProperty("resultMsg", out var messageElement)
                ? (messageElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (string.Equals(code, "Y", StringComparison.OrdinalIgnoreCase))
                return CpLoginTestResult.Ok(
                    message.Length > 0 ? message : "접속 가능합니다.");
            return CpLoginTestResult.Fail(
                message.Length > 0 ? message : "아이디 또는 패스워드를 확인해 주세요.");
        }
        catch (System.Text.Json.JsonException)
        {
            return CpLoginTestResult.Fail("로그인 응답을 해석할 수 없습니다. 사이트 구조가 바뀌었을 수 있습니다.");
        }
    }

    /// <summary>비밀번호 입력란이 들어 있는 폼을 로그인 폼으로 본다.</summary>
    private static string? FindLoginForm(string html) =>
        FormPattern.Matches(html)
            .Select(match => match.Value)
            .FirstOrDefault(form => form.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase) ||
                                    form.Contains("type='password'", StringComparison.OrdinalIgnoreCase) ||
                                    form.Contains("type=password", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 폼의 모든 input을 그대로 담고 아이디·비밀번호만 채워 넣는다.
    /// 숨은 값을 함께 보내야 CSRF 토큰이나 VIEWSTATE를 쓰는 사이트에서 로그인이 통과된다.
    /// </summary>
    private static List<KeyValuePair<string, string>>? BuildFormFields(
        string form,
        string userId,
        string password)
    {
        var fields = new List<KeyValuePair<string, string>>();
        string? passwordName = null;
        string? userIdName = null;

        foreach (Match input in InputPattern.Matches(form))
        {
            var attributes = ReadAttributes(input.Value);
            if (!attributes.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) continue;
            var type = attributes.GetValueOrDefault("type", "text").ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "reset") continue;
            if (type is "checkbox" or "radio" && !attributes.ContainsKey("checked")) continue;

            var value = attributes.GetValueOrDefault("value", string.Empty);
            if (type == "password")
            {
                passwordName = name;
                value = password;
            }
            else if (passwordName is null && userIdName is null && type is "text" or "email")
            {
                // 비밀번호 앞에 나오는 첫 텍스트 입력란을 아이디로 본다.
                userIdName = name;
                value = userId;
            }
            fields.Add(new KeyValuePair<string, string>(name, WebUtility.HtmlDecode(value)));
        }

        return passwordName is not null && userIdName is not null ? fields : null;
    }

    private static Uri ResolveAction(string form, Uri loginPageUrl)
    {
        var openingTag = form[..(form.IndexOf('>') + 1)];
        var action = ReadAttributes(openingTag).GetValueOrDefault("action", string.Empty);
        action = WebUtility.HtmlDecode(action).Trim();
        return string.IsNullOrEmpty(action)
            ? loginPageUrl
            : new Uri(loginPageUrl, action);
    }

    private static Dictionary<string, string> ReadAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern.Matches(tag))
            attributes[match.Groups["name"].Value] = match.Groups["value"].Value;
        return attributes;
    }

    /// <summary>
    /// 로그인 결과 판정.
    /// 응답에 로그인 폼이 다시 들어 있으면 로그인 화면으로 되돌아온 것으로 본다.
    /// 폼 전송 주소는 로그인 페이지 주소와 원래 다르므로, 그 둘이 아닌 곳으로
    /// 옮겨간 경우(로그인 후 리다이렉트)만 성공 신호로 함께 인정한다.
    /// </summary>
    private static CpLoginTestResult Evaluate(string html, Uri finalUrl, Uri actionUrl, Uri loginPageUrl)
    {
        var stillOnLoginForm = FindLoginForm(html) is not null;
        var redirectedElsewhere =
            !SameUrl(finalUrl, actionUrl) && !SameUrl(finalUrl, loginPageUrl);

        if (!stillOnLoginForm || redirectedElsewhere)
            return CpLoginTestResult.Ok($"접속 가능합니다. ({finalUrl.AbsoluteUri})");

        return CpLoginTestResult.Fail("아이디 또는 패스워드가 맞지 않아 로그인 화면으로 되돌아왔습니다.");
    }

    private static bool SameUrl(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
}
