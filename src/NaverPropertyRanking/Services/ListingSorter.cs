using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public enum ListingSortOrder
{
    RankAscending,
    RankDescending,
    DuplicateCountDescending,
    DuplicateCountAscending
}

public static class ListingSorter
{
    public static IReadOnlyList<Listing> Sort(
        IReadOnlyList<Listing> listings,
        IReadOnlyDictionary<string, RankingResult> rankingResults,
        ListingSortOrder sortOrder)
    {
        return sortOrder switch
        {
            ListingSortOrder.RankDescending => listings
                .OrderBy(listing => RankOf(listing, rankingResults) is null)
                .ThenByDescending(listing => RankOf(listing, rankingResults))
                .ThenBy(listing => listing.ArticleNo, StringComparer.Ordinal)
                .ToList(),
            ListingSortOrder.DuplicateCountDescending => listings
                .OrderBy(listing => DuplicateCountOf(listing, rankingResults) is null)
                .ThenByDescending(listing => DuplicateCountOf(listing, rankingResults))
                .ThenBy(listing => listing.ArticleNo, StringComparer.Ordinal)
                .ToList(),
            ListingSortOrder.DuplicateCountAscending => listings
                .OrderBy(listing => DuplicateCountOf(listing, rankingResults) is null)
                .ThenBy(listing => DuplicateCountOf(listing, rankingResults))
                .ThenBy(listing => listing.ArticleNo, StringComparer.Ordinal)
                .ToList(),
            _ => listings
                .OrderBy(listing => RankOf(listing, rankingResults) is null)
                .ThenBy(listing => RankOf(listing, rankingResults))
                .ThenBy(listing => listing.ArticleNo, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static int? RankOf(
        Listing listing,
        IReadOnlyDictionary<string, RankingResult> rankingResults) =>
        rankingResults.TryGetValue(listing.ArticleNo, out var result) && result.Success
            ? result.Rank
            : null;

    private static int? DuplicateCountOf(
        Listing listing,
        IReadOnlyDictionary<string, RankingResult> rankingResults) =>
        rankingResults.TryGetValue(listing.ArticleNo, out var result) && result.Success
            ? result.Total
            : null;
}
