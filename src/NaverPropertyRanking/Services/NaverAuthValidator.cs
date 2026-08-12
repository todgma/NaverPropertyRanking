using System.Text;
using System.Security.Cryptography;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class NaverAuthValidator
{
    public static string? GetError(AppSettings settings, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(settings.BearerToken)
            && string.IsNullOrWhiteSpace(settings.CookieHeader))
            return "네이버 인증값이 없습니다. 설정에서 최신 Bearer 토큰과 Cookie를 입력하세요.";

        if (string.IsNullOrWhiteSpace(settings.BearerToken))
            return "Bearer 토큰이 없습니다. 설정에서 최신 토큰을 입력하세요.";

        if (string.IsNullOrWhiteSpace(settings.CookieHeader))
            return "Cookie가 없습니다. 설정에서 최신 Cookie를 입력하세요.";

        return null;
    }

    public static string? GetError(ApiConfiguration configuration, DateTime? utcNow = null)
    {
        var realtorError = GetProfileError("중개인 매물 목록 API", configuration.RealtorArticleList, utcNow);
        if (realtorError is not null) return realtorError;
        return GetProfileError("랭킹 API", configuration.Ranking, utcNow);
    }

    public static string? GetProfileError(
        string profileName,
        ApiEndpointConfiguration profile,
        DateTime? utcNow = null)
    {
        profile.Headers.TryGetValue("Authorization", out var authorization);
        profile.Headers.TryGetValue("Cookie", out var cookie);
        if (string.IsNullOrWhiteSpace(authorization) && string.IsNullOrWhiteSpace(cookie))
            return $"{profileName} 인증값이 없습니다. appsettings.json의 Headers를 확인하세요.";
        if (string.IsNullOrWhiteSpace(authorization))
            return $"{profileName} Authorization이 없습니다.";
        if (string.IsNullOrWhiteSpace(cookie))
            return $"{profileName} Cookie가 없습니다.";

        return null;
    }

    public static string GetFingerprint(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BearerToken) && string.IsNullOrWhiteSpace(settings.CookieHeader))
            return string.Empty;
        var bytes = Encoding.UTF8.GetBytes($"{settings.BearerToken}\n{settings.CookieHeader}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string GetFingerprint(ApiConfiguration configuration)
    {
        static string Header(ApiEndpointConfiguration profile, string name) =>
            profile.Headers.TryGetValue(name, out var value) ? value : string.Empty;
        var material = string.Join("\n",
            Header(configuration.RealtorArticleList, "Authorization"),
            Header(configuration.RealtorArticleList, "Cookie"),
            Header(configuration.Ranking, "Authorization"),
            Header(configuration.Ranking, "Cookie"));
        return string.IsNullOrWhiteSpace(material)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
