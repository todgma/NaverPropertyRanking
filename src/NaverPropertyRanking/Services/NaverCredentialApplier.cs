using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 로그인 서버가 내려준 네이버 인증값을 API 설정에 채워 넣는다.
/// 인증값을 앱에 넣어 배포하지 않으므로 실행 중 메모리에서만 존재한다.
/// </summary>
public static class NaverCredentialApplier
{
    /// <summary>
    /// 인증값을 각 엔드포인트 프로필에 적용한다.
    /// 매물 목록·랭킹은 new.land.naver.com, 단지 광고는 fin.land.naver.com이라 쿠키가 다르다.
    /// 헤더가 비어 있는 프로필(매물 상세·단지 정보 등)은 호출 시 이 두 프로필을 그대로 물려받는다.
    /// </summary>
    public static bool Apply(ApiConfiguration configuration, NaverCredentials? credentials)
    {
        if (credentials is null || !credentials.HasValue) return false;

        ApplyLandHeaders(configuration.RealtorArticleList, credentials);
        ApplyLandHeaders(configuration.Ranking, credentials);
        ApplyLandHeaders(configuration.ComplexAdvertising, credentials);
        ApplyLandHeaders(configuration.ArticleDetail, credentials);
        ApplyLandHeaders(configuration.ComplexDetail, credentials);
        SetHeader(configuration.RealtorAdvertisement, "Cookie", credentials.EffectiveFinCookie);
        return true;
    }

    private static void ApplyLandHeaders(ApiEndpointConfiguration profile, NaverCredentials credentials)
    {
        SetHeader(profile, "Authorization", credentials.Authorization);
        SetHeader(profile, "Cookie", credentials.Cookie);
    }

    private static void SetHeader(ApiEndpointConfiguration profile, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile.Headers.Remove(name);
            return;
        }
        profile.Headers[name] = value;
    }
}
