namespace NaverPropertyRanking.Models;

public sealed record Listing(
    string ArticleNo,
    string Address,
    string TradeType,
    string Price,
    string RealtorName,
    string RealtorId,
    string ProviderName,
    string BuildingName,
    string FloorInfo,
    string Area,
    bool IsMine = false)
{
    public string ComplexNo { get; init; } = string.Empty;
}

public sealed record RankingResult(
    Listing OwnListing,
    int? Rank,
    int Total,
    string? SameAddressMinPrice,
    string? SameAddressMaxPrice,
    IReadOnlyList<Listing> Comparables,
    string? Error = null)
{
    public int? PreviousRank { get; init; }
    public bool Success => string.IsNullOrWhiteSpace(Error);
}

public sealed record ListingSnapshot(
    int? Rank,
    Dictionary<string, string> CompetitorPrices,
    int CompetitorCount,
    DateTime CheckedAtUtc);

public sealed record NotificationEvent(string Title, string Message);
