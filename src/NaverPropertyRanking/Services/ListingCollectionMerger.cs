using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed record ListingMergeResult(
    IReadOnlyList<Listing> Listings,
    IReadOnlyList<Listing> AddedListings);

public sealed record ListingReconciliationResult(
    IReadOnlyList<Listing> Listings,
    IReadOnlyList<Listing> AddedListings,
    IReadOnlyList<Listing> RemovedListings);

public static class ListingCollectionMerger
{
    public static ListingMergeResult AppendMissing(
        IReadOnlyList<Listing> currentListings,
        IReadOnlyList<Listing> latestListings)
    {
        var latest = latestListings
            .Where(listing => !string.IsNullOrWhiteSpace(listing.ArticleNo))
            .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First() with { IsMine = true })
            .ToDictionary(listing => listing.ArticleNo, StringComparer.Ordinal);
        var merged = currentListings
            .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(listing => latest.TryGetValue(listing.ArticleNo, out var refreshed)
                ? refreshed
                : listing)
            .ToList();
        var articleNumbers = merged
            .Select(listing => listing.ArticleNo)
            .ToHashSet(StringComparer.Ordinal);
        var added = new List<Listing>();

        foreach (var listing in latest.Values)
        {
            if (string.IsNullOrWhiteSpace(listing.ArticleNo) ||
                !articleNumbers.Add(listing.ArticleNo)) continue;

            var ownListing = listing with { IsMine = true };
            merged.Add(ownListing);
            added.Add(ownListing);
        }

        return new ListingMergeResult(merged, added);
    }

    public static ListingReconciliationResult Reconcile(
        IReadOnlyList<Listing> currentListings,
        IReadOnlyList<Listing> latestListings)
    {
        var current = currentListings
            .Where(listing => !string.IsNullOrWhiteSpace(listing.ArticleNo))
            .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(listing => listing.ArticleNo, StringComparer.Ordinal);
        var latest = latestListings
            .Where(listing => !string.IsNullOrWhiteSpace(listing.ArticleNo))
            .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First() with { IsMine = true })
            .ToList();
        var latestNumbers = latest
            .Select(listing => listing.ArticleNo)
            .ToHashSet(StringComparer.Ordinal);

        var added = latest
            .Where(listing => !current.ContainsKey(listing.ArticleNo))
            .ToList();
        var removed = current.Values
            .Where(listing => !latestNumbers.Contains(listing.ArticleNo))
            .ToList();

        return new ListingReconciliationResult(latest, added, removed);
    }
}
