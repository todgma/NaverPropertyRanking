using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class RankingTargetSelector
{
    public static IReadOnlyList<Listing> Select(
        IReadOnlyList<Listing> allListings,
        ISet<string> selectedArticleNumbers)
    {
        var selected = allListings
            .Where(listing => selectedArticleNumbers.Contains(listing.ArticleNo))
            .ToList();
        return selected.Count == 0 || selected.Count == allListings.Count
            ? allListings
            : selected;
    }

    public static bool ShouldRefreshOnClose(
        IReadOnlyList<Listing> allListings,
        ISet<string> selectedArticleNumbers,
        ISet<string> lastCompletedTargets,
        DateTime? lastCompletedUtc,
        DateTime utcNow)
    {
        if (allListings.Count == 0 || selectedArticleNumbers.Count == 0) return false;
        var targets = Select(allListings, selectedArticleNumbers);
        var sameTargetsJustCompleted = lastCompletedUtc is { } completedUtc
                                       && completedUtc >= utcNow.AddMinutes(-1)
                                       && lastCompletedTargets.SetEquals(targets.Select(x => x.ArticleNo));
        return !sameTargetsJustCompleted;
    }
}
