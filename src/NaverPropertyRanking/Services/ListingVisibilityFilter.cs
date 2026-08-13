using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class ListingVisibilityFilter
{
    public static IReadOnlyList<Listing> Apply(
        IReadOnlyList<Listing> listings,
        IReadOnlyDictionary<string, RankingResult> rankingResults,
        bool excludeSingleListings)
    {
        if (!excludeSingleListings) return listings.ToList();

        // 아직 순위를 조회하지 않았거나 조회에 실패한 매물은 숨기지 않는다.
        // 정상 결과가 1건 이하인 경우에만 단일매물로 판단하며, 이후 2건 이상이
        // 확인되면 다음 화면 갱신에서 자동으로 다시 표시된다.
        return listings
            .Where(listing =>
                !rankingResults.TryGetValue(listing.ArticleNo, out var result) ||
                !result.Success ||
                result.Total > 1)
            .ToList();
    }
}
