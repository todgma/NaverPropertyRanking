using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 직전 조회 결과(스냅샷)와 이번 조회 결과를 비교해
/// 목록에서 강조할 변동(단독→동일생성, 동일매물 금액변동)을 찾아낸다.
/// </summary>
public static class ListingChangeDetector
{
    /// <summary>단독매물이었다가 동일매물이 새로 생긴 경우 true.</summary>
    public static bool IsNewDuplicate(RankingResult current, ListingSnapshot? previous)
    {
        if (previous is null || !current.Success) return false;
        if (previous.CompetitorCount != 0) return false;
        return current.Comparables.Count(listing => !listing.IsMine) > 0;
    }

    /// <summary>이전 조회 대비 금액이 바뀐 동일매물 목록을 반환한다.</summary>
    public static IReadOnlyList<PriceChangeDetail> DetectPriceChanges(
        RankingResult current,
        ListingSnapshot? previous)
    {
        if (previous is null || !current.Success) return [];

        var changes = new List<PriceChangeDetail>();
        foreach (var competitor in current.Comparables.Where(listing => !listing.IsMine))
        {
            if (string.IsNullOrWhiteSpace(competitor.ArticleNo)) continue;
            if (!previous.CompetitorPrices.TryGetValue(competitor.ArticleNo, out var previousPrice)) continue;
            if (string.Equals(previousPrice, competitor.Price, StringComparison.Ordinal)) continue;

            changes.Add(new PriceChangeDetail(
                competitor.ArticleNo,
                competitor.RealtorName,
                previousPrice,
                competitor.Price,
                competitor.RegisteredDate,
                VerificationTypeFormatter.Format(competitor.VerificationTypeCode)));
        }

        return changes;
    }
}
