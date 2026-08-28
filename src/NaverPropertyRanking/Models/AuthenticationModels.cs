namespace NaverPropertyRanking.Models;

public sealed record AuthenticationSession(
    string UserId,
    string Name,
    string Token,
    string SessionId,
    DateTime? MembershipStart,
    DateTime? MembershipEnd,
    int AllowedPcCount,
    int CurrentPcCount,
    IReadOnlyList<string> Notices,
    int Grade = 1);

public sealed record AuthenticationResult(
    bool Success,
    string Message,
    AuthenticationSession? Session = null,
    string? Code = null,
    IReadOnlyList<string>? Notices = null,
    NaverCredentials? NaverCredentials = null);

/// <summary>
/// 로그인 서버가 내려주는 네이버 API 인증값.
/// 앱에 넣어 배포하지 않고 로그인·접속 확인 때마다 받아 메모리에서만 사용한다.
/// </summary>
public sealed record NaverCredentials(
    string Authorization,
    string Cookie,
    string FinCookie)
{
    public bool HasValue =>
        !string.IsNullOrWhiteSpace(Authorization) || !string.IsNullOrWhiteSpace(Cookie);

    /// <summary>
    /// fin.land.naver.com에 쓸 쿠키. 서버가 따로 주지 않으면 기본 쿠키를 그대로 쓴다.
    /// </summary>
    public string EffectiveFinCookie =>
        string.IsNullOrWhiteSpace(FinCookie) ? Cookie : FinCookie;
}
