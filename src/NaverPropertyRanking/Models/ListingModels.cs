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
    public string ArticleName { get; init; } = string.Empty;
    public string RealEstateType { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RegisteredDate { get; init; } = string.Empty;
    public string VerificationTypeCode { get; init; } = string.Empty;
    public int SameAddressCount { get; init; }
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

public sealed record ListingCacheEntry(
    string LoginId,
    string GroupId,
    DateTime SavedAtUtc,
    List<Listing> Listings,
    List<RankingResult> RankingResults);

public enum NotificationHighlight
{
    Neutral,
    RankUp,
    RankDown,
    Warning,
    PriceChange,
    NewDuplicate
}

public sealed record NotificationEvent(
    string Title,
    string Message,
    string ArticleNo = "",
    string ListingName = "",
    string TradeSummary = "",
    NotificationHighlight Highlight = NotificationHighlight.Neutral);
