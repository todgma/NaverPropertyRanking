using NaverPropertyRanking.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NaverPropertyRanking.Services;

public static class AdvertisementAnalysisService
{
    private static readonly Regex EokPattern = new(
        @"(?<eok>[0-9]+(?:\.[0-9]+)?)\s*억",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<AdvertisementComplex> GroupOwnedComplexes(IEnumerable<Listing> listings) =>
        listings
            .Where(listing => !string.IsNullOrWhiteSpace(listing.ComplexNo))
            .GroupBy(listing => listing.ComplexNo.Trim(), StringComparer.Ordinal)
            .Select(group => new AdvertisementComplex(
                group.Key,
                ComplexName(group),
                group.Count(),
                group.Select(listing => listing.ArticleNo)
                    .Where(articleNo => !string.IsNullOrWhiteSpace(articleNo))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(complex => complex.ComplexName, StringComparer.CurrentCulture)
            .ThenBy(complex => complex.ComplexNo, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<AdvertisedRealtor> SelectTopRealtors(IEnumerable<Listing> listings, int maximum = 3)
    {
        if (maximum <= 0) return [];
        var result = new List<AdvertisedRealtor>(maximum);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listings)
        {
            var key = RealtorKey(listing);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            result.Add(new AdvertisedRealtor(
                result.Count + 1,
                FirstNotEmpty(listing.RealtorName, "중개사명 없음"),
                listing.RealtorId,
                listing.ProviderName,
                listing.ArticleNo,
                listing.TradeType,
                listing.Price));
            if (result.Count >= maximum) break;
        }
        return result;
    }

    public static IReadOnlyList<RankedAdvertisementListing> SelectTopCompetitors(
        RankingResult rankingResult,
        int maximum = 3)
    {
        if (maximum <= 0) return [];

        var result = new List<RankedAdvertisementListing>(maximum);
        var seenRealtors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rankingResult.Comparables.Count; index++)
        {
            var listing = rankingResult.Comparables[index];
            if (listing.IsMine ||
                string.Equals(
                    listing.ArticleNo,
                    rankingResult.OwnListing.ArticleNo,
                    StringComparison.Ordinal))
                continue;

            // 동일 중개사가 여러 매물을 광고해도 가장 높은 노출순위 한 건만 표시한다.
            var realtorKey = RealtorKey(listing);
            if (string.IsNullOrWhiteSpace(realtorKey)) realtorKey = $"article:{listing.ArticleNo}";
            if (!seenRealtors.Add(realtorKey)) continue;

            result.Add(new RankedAdvertisementListing(
                result.Count + 1,
                index + 1,
                listing));
            if (result.Count >= maximum) break;
        }

        return result;
    }

    public static IReadOnlyList<AdvertisementComparisonRow> BuildComparisonRows(
        IEnumerable<RankingResult> rankingResults,
        int maximum = 3)
    {
        if (maximum <= 0) return [];

        return rankingResults
            .SelectMany(result => result.Comparables
                .Take(maximum)
                .Select((listing, index) => new AdvertisementComparisonRow(
                    result.OwnListing,
                    listing,
                    index + 1,
                    string.Equals(
                        result.OwnListing.ArticleNo,
                        listing.ArticleNo,
                        StringComparison.Ordinal),
                    BuildComparisonSummary(result.OwnListing, listing),
                    FormatPriceComparison(result.OwnListing, listing))))
            .ToList();
    }

    public static IReadOnlyList<AdvertisementFieldComparison> BuildFieldComparisons(
        IEnumerable<AdvertisementListingAnalysis> analyses,
        int maximumAdvertisements = 3)
    {
        var rows = new List<AdvertisementFieldComparison>();
        foreach (var analysis in analyses)
        {
            foreach (var advertisement in analysis.TopAdvertisements
                         .OrderBy(item => item.Rank)
                         .Take(Math.Max(0, maximumAdvertisements)))
            {
                var own = analysis.OwnDetail;
                var other = advertisement.Detail;
                void Add(
                    string category,
                    string field,
                    string ownValue,
                    string advertisementValue,
                    string priority,
                    Func<string, string, string>? comparer = null) =>
                    rows.Add(new AdvertisementFieldComparison(
                        analysis.RankingResult.OwnListing.ArticleNo,
                        advertisement.Rank,
                        other.Listing.ArticleNo,
                        other.Listing.RealtorName,
                        category,
                        field,
                        DisplayValue(ownValue),
                        DisplayValue(advertisementValue),
                        (comparer ?? CompareEquality)(ownValue, advertisementValue),
                        priority));

                Add("식별정보", "매물번호", own.Listing.ArticleNo, other.Listing.ArticleNo, "필수", CompareIdentifier);
                Add("식별정보", "단지번호", own.Listing.ComplexNo, other.Listing.ComplexNo, "필수");
                Add("식별정보", "매물명", own.Listing.ArticleName, other.Listing.ArticleName, "필수");
                Add("식별정보", "부동산 유형명", own.Listing.RealEstateType, other.Listing.RealEstateType, "필수");
                Add("식별정보", "거래유형명", own.Listing.TradeType, other.Listing.TradeType, "필수");
                //Add("위치정보", "소재지", own.Listing.Location, other.Listing.Location, "필수");
                //Add("위치정보", "법정동 코드", own.CortarNo, other.CortarNo, "낮음");
                Add("위치정보", "동·건물명", own.Listing.BuildingName, other.Listing.BuildingName, "필수");
                Add("면적·구조", "층 정보", own.Listing.FloorInfo, other.Listing.FloorInfo, "필수");
                Add("면적·구조", "해당 층", own.CorrespondingFloorCount, other.CorrespondingFloorCount, "높음");
                Add("면적·구조", "총 층", own.TotalFloorCount, other.TotalFloorCount, "보통");
                Add("면적·구조", "공급면적", own.SupplyArea, other.SupplyArea, "필수");
                Add("면적·구조", "전용면적", own.ExclusiveArea, other.ExclusiveArea, "필수");
                Add("면적·구조", "면적 표시명", own.AreaName, other.AreaName, "보통");
                Add("면적·구조", "방향", own.Direction, other.Direction, "높음");
                Add("면적·구조", "방 수", own.RoomCount, other.RoomCount, "높음", CompareNumber);
                Add("면적·구조", "욕실 수", own.BathroomCount, other.BathroomCount, "높음", CompareNumber);
                Add("면적·구조", "현관구조", own.EntranceType, other.EntranceType, "보통");
                Add("면적·구조", "건물구조", own.StructureType, other.StructureType, "낮음");
                Add("가격정보", "표시 거래금액", own.DisplayPrice, other.DisplayPrice, "필수", ComparePriceValue);
                Add("가격정보", "매매가", own.DealPrice, other.DealPrice, "필수", ComparePriceValue);
                Add("가격정보", "보증금", own.WarrantPrice, other.WarrantPrice, "필수", ComparePriceValue);
                Add("가격정보", "월세", own.RentPrice, other.RentPrice, "필수", ComparePriceValue);
                Add("가격정보", "가격변경 여부", own.IsPriceModification, other.IsPriceModification, "높음");
                Add("가격정보", "가격변경 상태", own.PriceChangeStatus, other.PriceChangeStatus, "높음");
                Add("비용·입주", "월 관리비", own.MonthlyManagementCost, other.MonthlyManagementCost, "높음", ComparePriceValue);
                Add("비용·입주", "관리비 포함 항목", own.ManagementCostIncludes, other.ManagementCostIncludes, "보통", CompareSet);
                Add("비용·입주", "융자금", own.LoanPrice, other.LoanPrice, "보통", ComparePriceValue);
                Add("비용·입주", "입주 가능 유형", own.MoveInType, other.MoveInType, "높음");
                Add("비용·입주", "입주 가능일", own.MoveInPossibleYmd, other.MoveInPossibleYmd, "높음", CompareDate);
                Add("시설정보", "총 주차대수", own.TotalParkingCount, other.TotalParkingCount, "보통", CompareNumber);
                Add("시설정보", "세대당 주차대수", own.ParkingCountPerHousehold, other.ParkingCountPerHousehold, "보통", CompareNumber);
                Add("시설정보", "냉난방 방식", own.HeatingAndCoolingSystem, other.HeatingAndCoolingSystem, "낮음");
                Add("시설정보", "난방 에너지", own.HeatingEnergy, other.HeatingEnergy, "낮음");
                Add("시설정보", "옵션", own.Options, other.Options, "높음", CompareSet);
                Add("시설정보", "생활시설", own.LifeFacilities, other.LifeFacilities, "보통", CompareSet);
                Add("시설정보", "보안시설", own.SecurityFacilities, other.SecurityFacilities, "보통", CompareSet);
                Add("시설정보", "기타시설", own.EtcFacilities, other.EtcFacilities, "낮음", CompareSet);
                Add("시설정보", "사용승인일", own.BuildingUseApprovalYmd, other.BuildingUseApprovalYmd, "낮음", CompareDate);
                Add("광고정보", "사진 수", NullableNumber(own.PhotoCount), NullableNumber(other.PhotoCount), "높음", CompareNumber);
                Add("광고정보", "매물 특징", own.Listing.Description, other.Listing.Description, "필수");
                Add("검증정보", "확인매물 유형 코드", own.VerificationTypeCode, other.VerificationTypeCode, "높음");
                Add("검증정보", "확인매물 유형명", own.VerificationTypeName, other.VerificationTypeName, "높음");
                Add(
                    "검증정보",
                    "검증방식",
                    VerificationTypeFormatter.Format(own.Listing.VerificationTypeCode),
                    VerificationTypeFormatter.Format(other.Listing.VerificationTypeCode),
                    "높음");
                Add("검증정보", "매물 확인일", own.ArticleConfirmYmd, other.ArticleConfirmYmd, "필수", CompareDate);
                Add("노출정보", "노출 시작일", own.ExposeStartYmd, other.ExposeStartYmd, "높음", CompareDate);
                Add("노출정보", "노출 종료일", own.ExposeEndYmd, other.ExposeEndYmd, "보통", CompareDate);
                Add("노출정보", "매물 상태", own.ArticleStatusCode, other.ArticleStatusCode, "높음");
                Add("중개사정보", "공인중개사 ID", own.Listing.RealtorId, other.Listing.RealtorId, "필수", CompareIdentifier);
                Add("중개사정보", "공인중개사명", own.Listing.RealtorName, other.Listing.RealtorName, "필수", CompareIdentifier);
                Add("제공사정보", "정보제공사 ID", own.ProviderId, other.ProviderId, "높음");
                Add("제공사정보", "정보제공사명", own.Listing.ProviderName, other.Listing.ProviderName, "높음");
                Add(
                    "순위정보",
                    "현재 노출순위",
                    analysis.RankingResult.Rank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    advertisement.ExposureRank.ToString(CultureInfo.InvariantCulture),
                    "필수",
                    CompareRank);
                Add(
                    "순위정보",
                    "동일매물 전체 수",
                    analysis.RankingResult.Total.ToString(CultureInfo.InvariantCulture),
                    analysis.RankingResult.Total.ToString(CultureInfo.InvariantCulture),
                    "필수",
                    CompareNumber);
                Add(
                    "계산정보",
                    "설명 글자 수",
                    own.Listing.Description.Length.ToString(CultureInfo.InvariantCulture),
                    other.Listing.Description.Length.ToString(CultureInfo.InvariantCulture),
                    "보통",
                    CompareNumber);
                Add(
                    "계산정보",
                    "사진 수 차이",
                    NullableNumber(own.PhotoCount),
                    NullableNumber(other.PhotoCount),
                    "보통",
                    CompareNumber);

                Add(
                    "조회정보",
                    "상세 API 상태",
                    string.IsNullOrWhiteSpace(own.Error) ? "정상" : own.Error,
                    string.IsNullOrWhiteSpace(other.Error) ? "정상" : other.Error,
                    "필수");
            }
        }
        return rows;
    }

    public static string BuildComparisonSummary(Listing ownListing, Listing advertisedListing)
    {
        if (string.Equals(ownListing.ArticleNo, advertisedListing.ArticleNo, StringComparison.Ordinal))
            return "기준 매물";

        var mismatches = new List<string>();
        var missing = new List<string>();
        Compare("단지번호", ownListing.ComplexNo, advertisedListing.ComplexNo, mismatches, missing);
        Compare("매물명", ownListing.ArticleName, advertisedListing.ArticleName, mismatches, missing);
        Compare("매물유형", ownListing.RealEstateType, advertisedListing.RealEstateType, mismatches, missing);
        Compare("거래유형", ownListing.TradeType, advertisedListing.TradeType, mismatches, missing);
        Compare("소재지", ownListing.Location, advertisedListing.Location, mismatches, missing);
        Compare("동·건물", ownListing.BuildingName, advertisedListing.BuildingName, mismatches, missing);
        Compare("층", ownListing.FloorInfo, advertisedListing.FloorInfo, mismatches, missing);
        Compare("면적", ownListing.Area, advertisedListing.Area, mismatches, missing);

        var parts = new List<string>();
        if (mismatches.Count > 0) parts.Add($"불일치: {string.Join(", ", mismatches)}");
        if (missing.Count > 0) parts.Add($"확인필요: {string.Join(", ", missing)}");
        return parts.Count == 0 ? "주요 필드 동일" : string.Join(" · ", parts);
    }

    public static string FormatPriceComparison(Listing ownListing, Listing advertisedListing)
    {
        if (!string.Equals(ownListing.TradeType, advertisedListing.TradeType, StringComparison.OrdinalIgnoreCase))
            return "거래유형 다름";

        var ownParts = ownListing.Price.Split('/', StringSplitOptions.TrimEntries);
        var advertisedParts = advertisedListing.Price.Split('/', StringSplitOptions.TrimEntries);
        if (!TryParsePricePart(ownParts.ElementAtOrDefault(0), out var ownPrimary) ||
            !TryParsePricePart(advertisedParts.ElementAtOrDefault(0), out var advertisedPrimary))
            return "비교 불가";

        var primaryLabel = ownParts.Length > 1 || advertisedParts.Length > 1 ? "보증금 " : string.Empty;
        var values = new List<string> { primaryLabel + FormatDifference(ownPrimary, advertisedPrimary) };
        if (ownParts.Length > 1 && advertisedParts.Length > 1 &&
            TryParsePricePart(ownParts[1], out var ownRent) &&
            TryParsePricePart(advertisedParts[1], out var advertisedRent))
            values.Add("월세 " + FormatDifference(ownRent, advertisedRent));
        return string.Join(" / ", values);
    }

    private static string CompareEquality(string ownValue, string advertisementValue)
    {
        if (string.IsNullOrWhiteSpace(ownValue) || string.IsNullOrWhiteSpace(advertisementValue)) return "정보 없음";
        return string.Equals(Normalize(ownValue), Normalize(advertisementValue), StringComparison.OrdinalIgnoreCase)
            ? "동일"
            : "불일치";
    }

    private static string CompareIdentifier(string ownValue, string advertisementValue)
    {
        if (string.IsNullOrWhiteSpace(ownValue) || string.IsNullOrWhiteSpace(advertisementValue)) return "정보 없음";
        return string.Equals(ownValue.Trim(), advertisementValue.Trim(), StringComparison.OrdinalIgnoreCase)
            ? "내 매물"
            : "비교 대상";
    }

    private static string CompareRank(string ownValue, string advertisementValue)
    {
        if (!int.TryParse(ownValue, out var ownRank) || !int.TryParse(advertisementValue, out var otherRank))
            return "정보 없음";
        if (ownRank == otherRank) return "동일 순위";
        var difference = otherRank - ownRank;
        return difference < 0
            ? $"내 매물보다 {Math.Abs(difference)}단계 상위"
            : $"내 매물보다 {difference}단계 하위";
    }

    private static string ComparePriceValue(string ownValue, string advertisementValue)
    {
        if (!TryParsePricePart(ownValue, out var ownPrice) ||
            !TryParsePricePart(advertisementValue, out var advertisementPrice))
            return "정보 없음";
        return FormatDifference(ownPrice, advertisementPrice);
    }

    private static string CompareNumber(string ownValue, string advertisementValue)
    {
        if (!decimal.TryParse(NumericText(ownValue), NumberStyles.Number, CultureInfo.InvariantCulture, out var ownNumber) ||
            !decimal.TryParse(NumericText(advertisementValue), NumberStyles.Number, CultureInfo.InvariantCulture, out var otherNumber))
            return "정보 없음";
        var difference = otherNumber - ownNumber;
        return difference switch
        {
            0 => "동일",
            > 0 => $"+{difference:0.##} · 많음",
            _ => $"{difference:0.##} · 적음"
        };
    }

    private static string CompareDate(string ownValue, string advertisementValue)
    {
        if (!TryParseDate(ownValue, out var ownDate) || !TryParseDate(advertisementValue, out var otherDate))
            return "정보 없음";
        var days = (otherDate.Date - ownDate.Date).Days;
        return days switch
        {
            0 => "동일",
            > 0 => $"{days}일 늦음",
            _ => $"{Math.Abs(days)}일 빠름"
        };
    }

    private static string CompareSet(string ownValue, string advertisementValue)
    {
        if (string.IsNullOrWhiteSpace(ownValue) || string.IsNullOrWhiteSpace(advertisementValue)) return "정보 없음";
        var own = SplitSet(ownValue);
        var other = SplitSet(advertisementValue);
        var added = other.Except(own, StringComparer.CurrentCultureIgnoreCase).ToList();
        var missing = own.Except(other, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (added.Count == 0 && missing.Count == 0) return "동일";
        var parts = new List<string>();
        if (added.Count > 0) parts.Add($"추가: {string.Join(", ", added)}");
        if (missing.Count > 0) parts.Add($"누락: {string.Join(", ", missing)}");
        return string.Join(" · ", parts);
    }

    private static void Compare(
        string label,
        string ownValue,
        string advertisedValue,
        ICollection<string> mismatches,
        ICollection<string> missing)
    {
        var own = Normalize(ownValue);
        var advertised = Normalize(advertisedValue);
        if (own.Length == 0 || advertised.Length == 0)
        {
            missing.Add(label);
            return;
        }
        if (!string.Equals(own, advertised, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(label);
    }

    private static bool TryParsePricePart(string? value, out decimal priceInTenThousands)
    {
        priceInTenThousands = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("만원", string.Empty, StringComparison.Ordinal)
            .Replace("원", string.Empty, StringComparison.Ordinal)
            .Trim();
        var match = EokPattern.Match(normalized);
        if (match.Success)
        {
            if (!decimal.TryParse(match.Groups["eok"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var eok))
                return false;
            priceInTenThousands = eok * 10_000m;
            var remainder = normalized[(match.Index + match.Length)..].Trim();
            if (remainder.Length == 0) return true;
            if (!decimal.TryParse(remainder, NumberStyles.Number, CultureInfo.InvariantCulture, out var remainderValue))
                return false;
            priceInTenThousands += remainderValue;
            return true;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out priceInTenThousands);
    }

    private static string FormatDifference(decimal ownPrice, decimal advertisedPrice)
    {
        var difference = advertisedPrice - ownPrice;
        if (difference == 0) return "동일";

        var direction = difference > 0 ? "비쌈" : "저렴";
        var rate = ownPrice == 0 ? (decimal?)null : difference / ownPrice * 100m;
        var signedDifference = difference > 0 ? $"+{difference:N0}" : $"{difference:N0}";
        var rateText = rate is null ? string.Empty : $" ({rate.Value:+0.0;-0.0;0.0}%)";
        return $"{signedDifference}만원{rateText} · {direction}";
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).Trim();

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "정보 없음" : value.Trim();

    private static string NullableNumber(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string NumericText(string value) =>
        new(value.Where(character => char.IsDigit(character) || character is '.' or '-').ToArray());

    private static bool TryParseDate(string value, out DateTime date)
    {
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy.MM.dd", "yyMMdd", "yy-MM-dd", "yy.MM.dd" };
        return DateTime.TryParseExact(
                   value.Trim(),
                   formats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out date) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private static IReadOnlyList<string> SplitSet(string value) =>
        value.Split([',', '/', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string ComplexName(IEnumerable<Listing> listings)
    {
        foreach (var listing in listings)
        {
            var value = FirstNotEmpty(listing.ArticleName, listing.Address);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "단지명 없음";
    }

    private static string RealtorKey(Listing listing) =>
        FirstNotEmpty(listing.RealtorId, listing.RealtorName);

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
