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
    /// <summary>CP(부동산포스)에서 가져온 동. 매물동기화 뒤 채워지고 로컬 캐시에 함께 저장된다.</summary>
    public string Dong { get; init; } = string.Empty;
    /// <summary>CP(부동산포스)에서 가져온 호.</summary>
    public string Ho { get; init; } = string.Empty;
    /// <summary>
    /// 동·호를 CP에서 한 번 조회했는지. 토지·상가처럼 동·호가 없는 매물을
    /// 동기화할 때마다 다시 조회하지 않기 위해 결과와 별개로 기록한다.
    /// </summary>
    public bool DongHoChecked { get; init; }
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

/// <summary>동일매물 금액변동 상세. 금액변동확인 팝업에 표시한다.</summary>
public sealed record PriceChangeDetail(
    string ArticleNo,
    string RealtorName,
    string PreviousPrice,
    string CurrentPrice,
    string RegisteredDate,
    string VerificationType);
