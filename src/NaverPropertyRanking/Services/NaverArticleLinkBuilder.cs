using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class NaverArticleLinkBuilder
{
    public static string Build(Listing listing)
    {
        var articleNo = Uri.EscapeDataString(listing.ArticleNo.Trim());
        if (!string.IsNullOrWhiteSpace(listing.ComplexNo))
        {
            var complexNo = Uri.EscapeDataString(listing.ComplexNo.Trim());
            return $"https://new.land.naver.com/complexes/{complexNo}?articleNo={articleNo}";
        }

        return $"https://new.land.naver.com/?articleNo={articleNo}";
    }
}
