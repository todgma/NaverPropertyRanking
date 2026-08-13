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
        var listingName = BuildListingName(current.OwnListing);
        var tradeSummary = JoinDistinct(" ", current.OwnListing.TradeType, current.OwnListing.Price);

        if (settings.NotifyEveryRankChange && previous.Rank != current.Rank)
        {
            events.Add(new NotificationEvent(
                "매물 랭킹 변경",
                $"{FormatRank(previous.Rank)} → {FormatRank(current.Rank)}",
                current.OwnListing.ArticleNo,
                listingName,
                tradeSummary,
                GetRankHighlight(previous.Rank, current.Rank)));
        }

        var crossedThreshold = current.Rank is not null
                               && current.Rank >= settings.RankThreshold
                               && (previous.Rank is null || previous.Rank < settings.RankThreshold);
        if (settings.NotifyRankThreshold && crossedThreshold)
        {
            events.Add(new NotificationEvent(
                "랭킹 기준 알림",
                $"현재 {current.Rank}위 · 설정 기준 {settings.RankThreshold}위 도달",
                current.OwnListing.ArticleNo,
                listingName,
                tradeSummary,
                NotificationHighlight.Warning));
        }

        if (settings.NotifyCompetitorPriceChange)
        {
            foreach (var competitor in competitors)
            {
                if (!previous.CompetitorPrices.TryGetValue(competitor.ArticleNo, out var oldPrice)) continue;
                if (string.Equals(oldPrice, competitor.Price, StringComparison.Ordinal)) continue;
                events.Add(new NotificationEvent(
                    "동일매물 가격 변경",
                    $"{competitor.RealtorName} ({competitor.ArticleNo}) 가격 {oldPrice} → {competitor.Price}",
                    current.OwnListing.ArticleNo,
                    listingName,
                    tradeSummary,
                    NotificationHighlight.PriceChange));
            }
        }

        if (settings.NotifyNewDuplicate && previous.CompetitorCount == 0 && competitors.Count > 0)
        {
            events.Add(new NotificationEvent(
                "단독매물 상태 변경",
                $"동일매물 {competitors.Count}건 신규 확인",
                current.OwnListing.ArticleNo,
                listingName,
                tradeSummary,
                NotificationHighlight.NewDuplicate));
        }

        return (snapshot, events);
    }

    private static string FormatRank(int? rank) => rank is null ? "순위 없음" : $"{rank}위";

    private static NotificationHighlight GetRankHighlight(int? previousRank, int? currentRank)
    {
        if (previousRank is null || currentRank is null) return NotificationHighlight.Neutral;
        return currentRank < previousRank
            ? NotificationHighlight.RankUp
            : NotificationHighlight.RankDown;
    }

    private static string BuildListingName(Listing listing)
    {
        var name = JoinDistinct(" ", listing.ArticleName, listing.BuildingName);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (!string.IsNullOrWhiteSpace(listing.Address)) return listing.Address;
        return listing.ArticleNo;
    }

    private static string JoinDistinct(string separator, params string[] values) =>
        string.Join(separator, values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal));
}
