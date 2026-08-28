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

    /// <summary>단지 페이지(https://new.land.naver.com/complexes/{단지번호}) 주소를 만든다.</summary>
    public static string BuildComplexLink(string complexNo) =>
        $"https://new.land.naver.com/complexes/{Uri.EscapeDataString(complexNo.Trim())}";
}
