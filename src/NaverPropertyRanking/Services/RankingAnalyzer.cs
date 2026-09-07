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
        var listingName = BuildListingInformation(current.OwnListing);
        var tradeSummary = JoinDistinct(" ", current.OwnListing.TradeType, current.OwnListing.Price);
        // 어떤 알림이든 어느 집인지 바로 알아볼 수 있게 동·호와 검증방식을 함께 넘긴다.
        var verificationType = VerificationTypeFormatter.Format(current.OwnListing.VerificationTypeCode);
        var dong = current.OwnListing.Dong;
        var ho = current.OwnListing.Ho;

        if (settings.NotifyEveryRankChange && previous.Rank != current.Rank)
        {
            // 순위만으로는 왜 밀렸는지 알기 어려워 홍보방식을 함께 넘긴다.
            // 문구에 섞지 않고 따로 두어 팝업에서 별도 칸으로 보여 준다.
            events.Add(new NotificationEvent(
                "매물 랭킹 변경",
                $"{FormatRank(previous.Rank)} → {FormatRank(current.Rank)}",
                current.OwnListing.ArticleNo,
                listingName,
                tradeSummary,
                GetRankHighlight(previous.Rank, current.Rank),
                verificationType,
                dong,
                ho));
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
                NotificationHighlight.Warning,
                verificationType,
                dong,
                ho));
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
                    NotificationHighlight.PriceChange,
                verificationType,
                dong,
                ho));
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
                NotificationHighlight.NewDuplicate,
                verificationType,
                dong,
                ho));
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

    private static string BuildListingInformation(Listing listing)
    {
        // The main list's "매물종류/설명" column displays Address. The parser
        // composes it from location, property type/name, building, description,
        // and floor information, so notifications must use the same value.
        if (!string.IsNullOrWhiteSpace(listing.Address)) return listing.Address;

        var name = JoinDistinct(" · ", listing.ArticleName, listing.BuildingName, listing.Description);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return listing.ArticleNo;
    }

    private static string JoinDistinct(string separator, params string[] values) =>
        string.Join(separator, values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal));
}
