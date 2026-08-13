namespace NaverPropertyRanking.Services;

public static class ListingProgressFormatter
{
    public static string Format(int completedCount, int totalCount, string? articleNo = null)
    {
        var safeTotal = Math.Max(0, totalCount);
        var safeCompleted = Math.Clamp(completedCount, 0, safeTotal);
        var text = $"전체 {safeTotal}건 중 조회건수/리스트건수: {safeCompleted}/{safeTotal}";
        return string.IsNullOrWhiteSpace(articleNo) ? text : $"{text} · {articleNo}";
    }
}
