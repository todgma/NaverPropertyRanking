using System.Net.Http.Json;
using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed class GoogleAuthenticationClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly GoogleAuthenticationConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private Task<string>? _publicIpTask;
    private Task? _serverWarmUpTask;

    public GoogleAuthenticationClient(
        GoogleAuthenticationConfiguration configuration,
        HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.RequestTimeoutSeconds, 5, 120));
    }

    public void StartWarmUp()
    {
        _publicIpTask ??= GetPublicIpAsync(CancellationToken.None);
        _serverWarmUpTask ??= WarmUpServerAsync();
    }

    public async Task<AuthenticationResult> SignUpAsync(
        string userId,
        string password,
        string name,
        CancellationToken cancellationToken)
    {
        var endpointError = GetEndpoint(out var endpoint);
        if (endpointError is not null) return endpointError;
        var payload = new
        {
            action = "signup",
            userId = userId.Trim(),
            password,
            name = name.Trim(),
            deviceId = DeviceIdentity.GetStableId(),
            deviceName = Environment.MachineName,
            ip = await GetCachedPublicIpAsync(cancellationToken),
            appVersion = Application.ProductVersion
        };
        return await PostAsync(endpoint!, payload, "signup", userId, cancellationToken);
    }

    public async Task<AuthenticationResult> LoginAsync(
        string userId,
        string password,
        CancellationToken cancellationToken)
    {
        var endpointError = GetEndpoint(out var endpoint);
        if (endpointError is not null) return endpointError;
        var payload = new
        {
            action = "login",
            userId = userId.Trim(),
            password,
            name = string.Empty,
            deviceId = DeviceIdentity.GetStableId(),
            deviceName = Environment.MachineName,
            ip = await GetCachedPublicIpAsync(cancellationToken),
            appVersion = Application.ProductVersion
        };
        return await PostAsync(endpoint!, payload, "login", userId, cancellationToken);
    }

    public Task<AuthenticationResult> HeartbeatAsync(
        AuthenticationSession session,
        CancellationToken cancellationToken) =>
        SendSessionRequestAsync("heartbeat", session, cancellationToken);

    public Task<AuthenticationResult> LogoutAsync(
        AuthenticationSession session,
        CancellationToken cancellationToken) =>
        SendSessionRequestAsync("logout", session, cancellationToken);

    public Task<AuthenticationResult> SaveMemberGroupAsync(
        AuthenticationSession session,
        string groupId,
        CancellationToken cancellationToken) =>
        SendSessionRequestAsync("saveMemberGroup", session, cancellationToken, groupId.Trim());

    private async Task<AuthenticationResult> SendSessionRequestAsync(
        string action,
        AuthenticationSession session,
        CancellationToken cancellationToken,
        string? groupId = null)
    {
        var endpointError = GetEndpoint(out var endpoint);
        if (endpointError is not null) return endpointError;
        var payload = new
        {
            action,
            sessionId = session.SessionId,
            token = session.Token,
            userId = session.UserId,
            groupId,
            deviceId = DeviceIdentity.GetStableId(),
            appVersion = Application.ProductVersion
        };
        return await PostAsync(endpoint!, payload, action, session.UserId, cancellationToken);
    }

    private AuthenticationResult? GetEndpoint(out Uri? endpoint)
    {
        endpoint = null;
        if (!_configuration.Enabled)
            return new AuthenticationResult(false, "로그인 기능이 비활성화되어 있습니다.");
        if (!Uri.TryCreate(_configuration.WebAppUrl, UriKind.Absolute, out endpoint))
            return new AuthenticationResult(false, "appsettings.json의 GoogleAuthentication.WebAppUrl을 확인하세요.");
        if (endpoint.AbsolutePath.EndsWith("/dev", StringComparison.OrdinalIgnoreCase))
            return new AuthenticationResult(false, "Apps Script 테스트용 /dev 주소는 사용할 수 없습니다. 새 배포에서 생성된 /exec 주소를 입력하세요.");
        return null;
    }

    private async Task<AuthenticationResult> PostAsync(
        Uri endpoint,
        object payload,
        string action,
        string userId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, JsonOptions, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new AuthenticationResult(false,
                    "Apps Script 웹 앱 접근이 거부되었습니다. 실행 사용자를 '나', 액세스 권한을 '모든 사용자'로 배포하고 /exec 주소를 사용하세요.",
                    Code: "ACCESS_DENIED");
            if (!response.IsSuccessStatusCode)
                return new AuthenticationResult(false, $"로그인 서버 응답 오류: HTTP {(int)response.StatusCode}",
                    Code: $"HTTP_{(int)response.StatusCode}");
            if (body.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
                || body.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
                return new AuthenticationResult(false,
                    "Google 로그인 페이지가 반환되었습니다. 웹 앱 액세스 권한을 익명 사용자를 포함한 '모든 사용자'로 변경하세요.",
                    Code: "GOOGLE_LOGIN_REQUIRED");

            var apiResponse = JsonSerializer.Deserialize<ApiResponse>(body, JsonOptions);
            if (apiResponse is null)
                return new AuthenticationResult(false, "로그인 서버 응답을 해석할 수 없습니다.", Code: "INVALID_RESPONSE");
            if (!apiResponse.Success)
                return new AuthenticationResult(false, apiResponse.Message ?? "요청에 실패했습니다.", Code: apiResponse.Code);
            var notices = apiResponse.Notices is null
                ? new List<string> { "공지사항을 불러오지 못했습니다. Apps Script를 최신 Code.gs로 다시 배포하세요." }
                : NormalizeNotices(apiResponse.Notices);
            if (!string.Equals(action, "login", StringComparison.OrdinalIgnoreCase))
                return new AuthenticationResult(
                    true,
                    apiResponse.Message ?? "요청이 완료되었습니다.",
                    Code: apiResponse.Code,
                    Notices: apiResponse.Notices is null ? null : notices);

            var session = new AuthenticationSession(
                apiResponse.UserId ?? userId.Trim(),
                apiResponse.Name ?? string.Empty,
                apiResponse.Token ?? string.Empty,
                apiResponse.SessionId ?? string.Empty,
                ParseDate(apiResponse.MembershipStart),
                ParseDate(apiResponse.MembershipEnd),
                apiResponse.AllowedPcCount,
                apiResponse.CurrentPcCount,
                notices,
                apiResponse.Grade > 0 ? apiResponse.Grade : 1);
            if (string.IsNullOrWhiteSpace(session.Token) || string.IsNullOrWhiteSpace(session.SessionId))
                return new AuthenticationResult(false, "로그인 서버가 세션 정보를 반환하지 않았습니다. Code.gs를 새 버전으로 배포하세요.",
                    Code: "MISSING_SESSION");
            return new AuthenticationResult(true, apiResponse.Message ?? "로그인되었습니다.", session, apiResponse.Code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AuthenticationResult(false, "로그인 서버 응답 시간이 초과되었습니다.", Code: "TIMEOUT");
        }
        catch (HttpRequestException ex)
        {
            return new AuthenticationResult(false, $"로그인 서버에 연결할 수 없습니다: {ex.Message}", Code: "NETWORK_ERROR");
        }
        catch (JsonException ex)
        {
            return new AuthenticationResult(false, $"로그인 서버 응답 형식이 올바르지 않습니다: {ex.Message}", Code: "INVALID_RESPONSE");
        }
    }

    private async Task<string> GetPublicIpAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_configuration.PublicIpEndpoint, UriKind.Absolute, out var endpoint))
            return "확인불가";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var value = await _httpClient.GetStringAsync(endpoint, timeout.Token);
            value = value.Trim();
            return value.Length is > 0 and <= 64 ? value : "확인불가";
        }
        catch
        {
            return "확인불가";
        }
    }

    private async Task<string> GetCachedPublicIpAsync(CancellationToken cancellationToken)
    {
        StartWarmUp();
        var task = _publicIpTask!;
        return await task.WaitAsync(cancellationToken);
    }

    private async Task WarmUpServerAsync()
    {
        if (GetEndpoint(out var endpoint) is not null || endpoint is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _httpClient.GetAsync(endpoint, timeout.Token);
        }
        catch
        {
            // 워밍업 실패는 실제 로그인 요청에서 다시 처리합니다.
        }
    }

    private static List<string> NormalizeNotices(IEnumerable<string>? notices) =>
        notices?
            .Where(notice => !string.IsNullOrWhiteSpace(notice))
            .Select(notice => notice.Trim())
            .ToList() ?? [];

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;

    public void Dispose() => _httpClient.Dispose();

    private sealed class ApiResponse
    {
        public bool Success { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
        public string? UserId { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
        public string? SessionId { get; set; }
        public string? MembershipStart { get; set; }
        public string? MembershipEnd { get; set; }
        public int AllowedPcCount { get; set; }
        public int CurrentPcCount { get; set; }
        public int Grade { get; set; } = 1;
        public List<string>? Notices { get; set; }
    }
}
