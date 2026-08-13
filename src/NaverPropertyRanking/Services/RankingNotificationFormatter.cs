using System.Text;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class RankingNotificationFormatter
{
    private static readonly string[] PreferredOrder =
    [
        "매물 랭킹 변경",
        "랭킹 기준 알림",
        "동일매물 가격 변경",
        "단독매물 상태 변경"
    ];

    public static string Format(IReadOnlyList<NotificationEvent> events)
    {
        if (events.Count == 0) return "변동 내역이 없습니다.";

        var orderedEvents = events
            .OrderBy(item => Array.IndexOf(PreferredOrder, item.Title) is var index && index >= 0
                ? index
                : int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ThenBy(item => item.ArticleNo, StringComparer.Ordinal)
            .ToList();
        var builder = new StringBuilder();
        foreach (var item in orderedEvents)
        {
            var listingName = FirstNotEmpty(item.ListingName, item.ArticleNo, "매물정보 없음");
            var tradeSummary = FirstNotEmpty(item.TradeSummary, "거래정보 없음");
            builder.AppendLine($"{listingName} | {tradeSummary} | [{item.Title}] {item.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FirstNotEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value));
}
