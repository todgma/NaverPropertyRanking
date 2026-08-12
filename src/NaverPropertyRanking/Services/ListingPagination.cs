using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class ListingPagination
{
    private static readonly int[] AllowedPageSizes = [0, 10, 20, 30];

    public static int NormalizePageSize(int pageSize) =>
        AllowedPageSizes.Contains(pageSize) ? pageSize : 10;

    public static int GetPageCount(int totalCount, int pageSize)
    {
        var effectiveSize = GetEffectivePageSize(totalCount, pageSize);
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)effectiveSize));
    }

    public static IReadOnlyList<Listing> GetPage(
        IReadOnlyList<Listing> listings,
        int page,
        int pageSize)
    {
        var effectiveSize = GetEffectivePageSize(listings.Count, pageSize);
        var pageCount = GetPageCount(listings.Count, pageSize);
        var normalizedPage = Math.Clamp(page, 1, pageCount);
        return listings.Skip((normalizedPage - 1) * effectiveSize).Take(effectiveSize).ToList();
    }

    private static int GetEffectivePageSize(int totalCount, int pageSize)
    {
        var normalized = NormalizePageSize(pageSize);
        return normalized == 0 ? Math.Max(1, totalCount) : normalized;
    }
}
