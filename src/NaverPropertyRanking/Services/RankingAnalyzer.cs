using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class RankingAnalyzer
{
    public static (ListingSnapshot Snapshot, IReadOnlyList<NotificationEvent> Events) Compare(
        RankingResult current,
        ListingSnapshot? previous,
        AppSettings settings)
    {
        var competitors = current.Comparables.Where(x => !x.IsMine).ToList();
        var prices = competitors
            .Where(x => !string.IsNullOrWhiteSpace(x.ArticleNo))
            .ToDictionary(x => x.ArticleNo, x => x.Price);
        var snapshot = new ListingSnapshot(current.Rank, prices, competitors.Count, DateTime.UtcNow);
        if (previous is null || !current.Success) return (snapshot, []);

        var events = new List<NotificationEvent>();
        var label = string.IsNullOrWhiteSpace(current.OwnListing.Address)
            ? current.OwnListing.ArticleNo
            : current.OwnListing.Address;

        if (settings.NotifyEveryRankChange && previous.Rank != current.Rank)
        {
            events.Add(new NotificationEvent(
                "매물 랭킹 변경",
                $"{label}: {FormatRank(previous.Rank)} → {FormatRank(current.Rank)}"));
        }

        var crossedThreshold = current.Rank is not null
                               && current.Rank >= settings.RankThreshold
                               && (previous.Rank is null || previous.Rank < settings.RankThreshold);
        if (settings.NotifyRankThreshold && crossedThreshold)
        {
            events.Add(new NotificationEvent(
                "랭킹 기준 알림",
                $"{label}: 현재 {current.Rank}위로 설정 기준({settings.RankThreshold}위 이상 숫자)에 도달했습니다."));
        }

        if (settings.NotifyCompetitorPriceChange)
        {
            foreach (var competitor in competitors)
            {
                if (!previous.CompetitorPrices.TryGetValue(competitor.ArticleNo, out var oldPrice)) continue;
                if (string.Equals(oldPrice, competitor.Price, StringComparison.Ordinal)) continue;
                events.Add(new NotificationEvent(
                    "동일매물 가격 변경",
                    $"{competitor.RealtorName} ({competitor.ArticleNo}): {oldPrice} → {competitor.Price}"));
            }
        }

        if (settings.NotifyNewDuplicate && previous.CompetitorCount == 0 && competitors.Count > 0)
        {
            events.Add(new NotificationEvent(
                "단독매물 상태 변경",
                $"{label}: 동일매물이 {competitors.Count}건 새로 확인되었습니다."));
        }

        return (snapshot, events);
    }

    private static string FormatRank(int? rank) => rank is null ? "순위 없음" : $"{rank}위";
}
