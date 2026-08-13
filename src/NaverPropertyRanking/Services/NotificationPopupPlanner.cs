using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public sealed record NotificationPopupDefinition(
    string WindowTitle,
    string Headline,
    IReadOnlyList<NotificationEvent> Events);

public static class NotificationPopupPlanner
{
    private static readonly string[] PreferredOrder =
    [
        "매물 랭킹 변경",
        "랭킹 기준 알림",
        "동일매물 가격 변경",
        "단독매물 상태 변경"
    ];

    public static IReadOnlyList<NotificationPopupDefinition> Create(
        IReadOnlyList<NotificationEvent> events)
    {
        if (events.Count == 0)
        {
            return
            [
                new NotificationPopupDefinition(
                    "순위조회 완료",
                    "순위조회가 완료되었습니다.",
                    [])
            ];
        }

        return events
            .GroupBy(item => item.Title)
            .OrderBy(group => PreferredIndex(group.Key))
            .ThenBy(group => group.Key, StringComparer.CurrentCulture)
            .Select(group =>
            {
                var presentation = PresentationFor(group.Key);
                return new NotificationPopupDefinition(
                    presentation.WindowTitle,
                    presentation.Headline,
                    group.ToList());
            })
            .ToList();
    }

    public static IReadOnlySet<string> SelectTitlesToReplace(
        IEnumerable<string> openPopupTitles,
        IReadOnlyList<NotificationPopupDefinition> newPopups)
    {
        var newTitles = newPopups
            .Select(popup => popup.WindowTitle)
            .ToHashSet(StringComparer.Ordinal);
        return openPopupTitles
            .Where(newTitles.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int PreferredIndex(string title)
    {
        var index = Array.IndexOf(PreferredOrder, title);
        return index < 0 ? int.MaxValue : index;
    }

    private static (string WindowTitle, string Headline) PresentationFor(string title) => title switch
    {
        "매물 랭킹 변경" => ("순위변동 알림", "매물 순위변동이 확인되었습니다."),
        "랭킹 기준 알림" => ("순위기준 알림", "설정한 순위기준에 도달한 매물이 있습니다."),
        "동일매물 가격 변경" => ("동일매물 가격변동 알림", "동일매물 가격변동이 확인되었습니다."),
        "단독매물 상태 변경" => ("동일매물 추가 알림", "단독매물에 동일매물이 추가되었습니다."),
        _ => ($"{title} 알림", $"{title} 변동이 확인되었습니다.")
    };
}
