namespace NaverPropertyRanking.Services;

public enum RankMovement
{
    None,
    Up,
    Down
}

public static class RankPresentation
{
    public static RankMovement GetMovement(int? previousRank, int? currentRank)
    {
        if (previousRank is null || currentRank is null || previousRank == currentRank)
            return RankMovement.None;
        return currentRank < previousRank ? RankMovement.Up : RankMovement.Down;
    }

    public static string FormatPrevious(int? rank) => rank is null ? "-" : $"{rank}위";

    public static string FormatCurrent(int? previousRank, int? currentRank)
    {
        if (currentRank is null) return "-";
        var difference = previousRank is null ? 0 : Math.Abs(previousRank.Value - currentRank.Value);
        return GetMovement(previousRank, currentRank) switch
        {
            RankMovement.Up => $"{currentRank}위 ↑{difference}",
            RankMovement.Down => $"{currentRank}위 ↓{difference}",
            _ => $"{currentRank}위"
        };
    }
}
