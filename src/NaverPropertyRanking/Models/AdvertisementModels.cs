namespace NaverPropertyRanking.Models;

public sealed record AdvertisementComplex(
    string ComplexNo,
    string ComplexName,
    int OwnedListingCount,
    IReadOnlyList<string>? ArticleNumbers = null);

/// <summary>
/// 네이버 단지 정보 API(/api/complexes/{complexNo}) 응답에서 추린 단지 기본 정보.
/// 모든 값은 표시용 문자열이며 응답에 없으면 빈 값으로 남긴다.
/// </summary>
public sealed record ComplexInformation(
    string ComplexNo,
    string ComplexName)
{
    public string HouseholdSummary { get; init; } = string.Empty;
    public string FloorRange { get; init; } = string.Empty;
    public string UseApproveDate { get; init; } = string.Empty;
    public string ParkingSummary { get; init; } = string.Empty;
    public string FloorAreaRatio { get; init; } = string.Empty;
    public string BuildingCoverageRatio { get; init; } = string.Empty;
    public string ConstructionCompany { get; init; } = string.Empty;
    public string Heating { get; init; } = string.Empty;
    public string ManagementOfficeTel { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string RoadAddress { get; init; } = string.Empty;
    public string AreaNames { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

/// <summary>단지 광고 API(front-api)에서 추린 광고 순위별 중개인 정보.</summary>
public sealed record ComplexAdvertisementRealtor(string RealtorName, string RealtorId);

public sealed record AdvertisedRealtor(
    int Rank,
    string RealtorName,
    string RealtorId,
    string ProviderName,
    string ArticleNo,
    string TradeType,
    string Price);

public sealed record AdvertisementComparisonRow(
    Listing OwnListing,
    Listing AdvertisedListing,
    int Rank,
    bool IsReferenceListing,
    string ComparisonSummary,
    string PriceComparison);

public sealed record ArticleComparisonDetail(Listing Listing)
{
    public string CortarNo { get; init; } = string.Empty;
    public string CorrespondingFloorCount { get; init; } = string.Empty;
    public string TotalFloorCount { get; init; } = string.Empty;
    public string SupplyArea { get; init; } = string.Empty;
    public string ExclusiveArea { get; init; } = string.Empty;
    public string AreaName { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string RoomCount { get; init; } = string.Empty;
    public string BathroomCount { get; init; } = string.Empty;
    public string EntranceType { get; init; } = string.Empty;
    public string StructureType { get; init; } = string.Empty;
    public string DisplayPrice { get; init; } = string.Empty;
    public string DealPrice { get; init; } = string.Empty;
    public string WarrantPrice { get; init; } = string.Empty;
    public string RentPrice { get; init; } = string.Empty;
    public string IsPriceModification { get; init; } = string.Empty;
    public string PriceChangeStatus { get; init; } = string.Empty;
    public string MonthlyManagementCost { get; init; } = string.Empty;
    public string ManagementCostIncludes { get; init; } = string.Empty;
    public string LoanPrice { get; init; } = string.Empty;
    public string MoveInType { get; init; } = string.Empty;
    public string MoveInPossibleYmd { get; init; } = string.Empty;
    public string TotalParkingCount { get; init; } = string.Empty;
    public string ParkingCountPerHousehold { get; init; } = string.Empty;
    public string HeatingAndCoolingSystem { get; init; } = string.Empty;
    public string HeatingEnergy { get; init; } = string.Empty;
    public string Options { get; init; } = string.Empty;
    public string LifeFacilities { get; init; } = string.Empty;
    public string SecurityFacilities { get; init; } = string.Empty;
    public string EtcFacilities { get; init; } = string.Empty;
    public string BuildingUseApprovalYmd { get; init; } = string.Empty;
    public int? PhotoCount { get; init; }
    public string VerificationTypeCode { get; init; } = string.Empty;
    public string VerificationTypeName { get; init; } = string.Empty;
    public string ArticleConfirmYmd { get; init; } = string.Empty;
    public string ExposeStartYmd { get; init; } = string.Empty;
    public string ExposeEndYmd { get; init; } = string.Empty;
    public string ArticleStatusCode { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

public sealed record RankedAdvertisementListing(
    int Rank,
    int ExposureRank,
    Listing Listing);

public sealed record RankedArticleComparison(
    int Rank,
    int ExposureRank,
    ArticleComparisonDetail Detail);

public sealed record AdvertisementListingAnalysis(
    RankingResult RankingResult,
    ArticleComparisonDetail OwnDetail,
    IReadOnlyList<RankedArticleComparison> TopAdvertisements);

public sealed record AdvertisementFieldComparison(
    string CheckedArticleNo,
    int AdvertisementRank,
    string AdvertisementArticleNo,
    string RealtorName,
    string Category,
    string FieldName,
    string OwnValue,
    string AdvertisementValue,
    string Result,
    string Priority);

