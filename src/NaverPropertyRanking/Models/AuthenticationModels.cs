namespace NaverPropertyRanking.Models;

public sealed record AuthenticationSession(
    string UserId,
    string Name,
    string Token,
    string SessionId,
    DateTime? MembershipStart,
    DateTime? MembershipEnd,
    int AllowedPcCount,
    int CurrentPcCount);

public sealed record AuthenticationResult(
    bool Success,
    string Message,
    AuthenticationSession? Session = null,
    string? Code = null);
