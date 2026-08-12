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

        var groups = events
            .GroupBy(item => item.Title)
            .OrderBy(group => Array.IndexOf(PreferredOrder, group.Key) is var index && index >= 0
                ? index
                : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.CurrentCulture)
            .ToList();
        var builder = new StringBuilder();
        foreach (var group in groups)
        {
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine($"[{group.Key}] {group.Count()}건");
            foreach (var item in group)
                builder.AppendLine($"• {item.Message}");
        }

        return builder.ToString().TrimEnd();
    }
}
