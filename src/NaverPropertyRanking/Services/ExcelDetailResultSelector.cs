using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class ExcelDetailResultSelector
{
    /// <summary>
    /// 상세 시트에 트리로 출력할 순위 결과를 선택한다.
    /// 동일매물이 2건 이상인 결과만 대상으로 하며, 내 매물 여러 건이 서로 동일매물이면
    /// 목록 순서상 가장 앞선 매물 한 건만 트리를 만든다. 나머지 매물은 이미 그 트리의
    /// 하위에 순위와 함께 표시되므로 별도 트리를 생성하지 않는다.
    /// </summary>
    public static IReadOnlyList<RankingResult> Select(IEnumerable<RankingResult> results)
    {
        var selected = new List<RankingResult>();
        var alreadyShownArticleNumbers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            if (result.Total < 2) continue;

            var articleNo = result.OwnListing.ArticleNo;
            if (!string.IsNullOrWhiteSpace(articleNo) &&
                alreadyShownArticleNumbers.Contains(articleNo)) continue;

            selected.Add(result);
            foreach (var comparable in result.Comparables)
                if (!string.IsNullOrWhiteSpace(comparable.ArticleNo))
                    alreadyShownArticleNumbers.Add(comparable.ArticleNo);
        }

        return selected;
    }
}
