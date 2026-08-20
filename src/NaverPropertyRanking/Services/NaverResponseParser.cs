using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class NaverResponseParser
{
    private static readonly Regex ComplexUrlPattern = new(
        @"/complexes/(?<no>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (IReadOnlyList<Listing> Listings, bool? IsMoreData) ParseArticleResponse(
        string json,
        ISet<string>? ownArticleNumbers = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var array = FindArticleArray(root);
        var listings = new List<Listing>();

        if (array is { ValueKind: JsonValueKind.Array })
        {
            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var listing = ParseListing(item, ownArticleNumbers);
                if (!string.IsNullOrWhiteSpace(listing.ArticleNo)) listings.Add(listing);
            }
        }

        return (listings, FindBoolean(root, "isMoreData", "moreDataYn"));
    }

    public static (string? MinPrice, string? MaxPrice) ParseSameAddressPrices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var array = FindArticleArray(document.RootElement);
        if (array is not { ValueKind: JsonValueKind.Array } || array.Value.GetArrayLength() == 0)
            return (null, null);

        var first = array.Value[0];
        return (GetText(first, "sameAddrMinPrc"), GetText(first, "sameAddrMaxPrc"));
    }

    public static (string ComplexNo, string ComplexName) ParseArticleComplexIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var detail = root;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(root, "articleDetail", out var articleDetail) &&
            articleDetail.ValueKind == JsonValueKind.Object)
            detail = articleDetail;

        return (
            GetText(detail, "complexNo", "complexNumber") ??
            FindTextRecursively(root, "complexNo", "complexNumber", "hscpNo") ?? string.Empty,
            GetText(detail, "articleName", "complexName", "atclNm") ??
            FindTextRecursively(root, "articleName", "complexName", "atclNm") ?? string.Empty);
    }

    public static ArticleComparisonDetail ParseArticleComparisonDetail(string json, Listing fallbackListing)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var articleNo = FindTextRecursively(root, "articleNo", "articleNumber", "atclNo") ?? fallbackListing.ArticleNo;
        var articleName = FindTextRecursively(root, "articleName", "complexName", "atclNm") ?? fallbackListing.ArticleName;
        var realEstateType = FindTextRecursively(root, "realEstateTypeName", "rletTpNm") ?? fallbackListing.RealEstateType;
        var tradeType = FindTextRecursively(root, "tradeTypeName", "tradeType", "tradTpNm") ?? fallbackListing.TradeType;
        var buildingName = FindTextRecursively(root, "buildingName", "dongName", "bildNm") ?? fallbackListing.BuildingName;
        var floorInfo = FindTextRecursively(root, "floorInfo", "flrInfo", "flrNm") ?? fallbackListing.FloorInfo;
        var location = FindTextRecursively(
            root,
            "exposeAddress",
            "cortarAddress",
            "roadAddress",
            "roadAddressName",
            "address") ?? fallbackListing.Location;
        var description = FindTextRecursively(
            root,
            "articleFeatureDesc",
            "articleFeatureDescription",
            "articleDescription",
            "description") ?? fallbackListing.Description;
        var dealOrWarrantPrice = FindTextRecursively(root, "dealOrWarrantPrc", "dealOrWrterPrc", "priceText") ?? fallbackListing.Price;
        var rentPrice = FindTextRecursively(root, "rentPrc", "rentPrice", "monthlyRent") ?? string.Empty;
        var displayPrice = string.IsNullOrWhiteSpace(rentPrice) || dealOrWarrantPrice.Contains('/', StringComparison.Ordinal)
            ? dealOrWarrantPrice
            : $"{dealOrWarrantPrice}/{rentPrice}";
        var areaName = FindTextRecursively(root, "areaName") ?? fallbackListing.Area;
        var mergedListing = new Listing(
            articleNo,
            FirstNotEmpty(location, fallbackListing.Address),
            tradeType,
            FirstNotEmpty(displayPrice, fallbackListing.Price),
            FindTextRecursively(root, "realtorName", "brokerName", "rltrNm") ?? fallbackListing.RealtorName,
            FindTextRecursively(root, "realtorId", "brokerId", "realtorNo", "rltrId") ?? fallbackListing.RealtorId,
            FindTextRecursively(root, "cpName", "providerName") ?? fallbackListing.ProviderName,
            buildingName,
            floorInfo,
            FirstNotEmpty(areaName, fallbackListing.Area),
            fallbackListing.IsMine)
        {
            ComplexNo = FindTextRecursively(root, "complexNo", "complexNumber", "hscpNo") ?? fallbackListing.ComplexNo,
            ArticleName = articleName,
            RealEstateType = realEstateType,
            Location = location,
            Description = description,
            RegisteredDate = FindTextRecursively(
                root,
                "articleConfirmYMD",
                "articleConfirmYmd",
                "articleConfirmDate",
                "confirmYmd") ?? fallbackListing.RegisteredDate,
            VerificationTypeCode = FindTextRecursively(root, "verificationTypeCode") ?? fallbackListing.VerificationTypeCode,
            SameAddressCount = FindIntRecursively(root, "sameAddrCnt", "sameAddressCount") ?? fallbackListing.SameAddressCount
        };

        return new ArticleComparisonDetail(mergedListing)
        {
            CortarNo = FindTextRecursively(root, "cortarNo") ?? string.Empty,
            CorrespondingFloorCount = FindTextRecursively(root, "correspondingFloorCount") ?? FloorPart(floorInfo, 0),
            TotalFloorCount = FindTextRecursively(root, "totalFloorCount", "upperGroundFloorCount") ?? FloorPart(floorInfo, 1),
            SupplyArea = FindTextRecursively(root, "area1", "supplySpace") ?? string.Empty,
            ExclusiveArea = FindTextRecursively(root, "area2", "exclusiveArea", "exclusiveSpace", "spc2") ?? fallbackListing.Area,
            AreaName = areaName,
            Direction = FindTextRecursively(root, "direction", "directionTypeName") ?? string.Empty,
            RoomCount = FindTextRecursively(root, "roomCount") ?? string.Empty,
            BathroomCount = FindTextRecursively(root, "bathroomCount") ?? string.Empty,
            EntranceType = FindTextRecursively(root, "entranceTypeName", "entranceTypeCode") ?? string.Empty,
            StructureType = FindTextRecursively(root, "structureTypeName", "structureTypeCode") ?? string.Empty,
            DisplayPrice = displayPrice,
            DealPrice = FindTextRecursively(root, "dealPrice") ??
                        (tradeType.Contains("매매", StringComparison.Ordinal) ? dealOrWarrantPrice : string.Empty),
            WarrantPrice = FindTextRecursively(root, "warrantPrice") ??
                           (!tradeType.Contains("매매", StringComparison.Ordinal) ? dealOrWarrantPrice : string.Empty),
            RentPrice = rentPrice,
            IsPriceModification = FindTextRecursively(root, "isPriceModification") ?? string.Empty,
            PriceChangeStatus = FindTextRecursively(root, "priceChangeState", "priceChangeStatus") ?? string.Empty,
            MonthlyManagementCost = FindTextRecursively(root, "monthlyManagementCost") ?? string.Empty,
            ManagementCostIncludes = FindJoinedValues(root, "monthlyManagementCostIncludeItemName") ?? string.Empty,
            LoanPrice = FindTextRecursively(root, "loanPrice", "loan") ?? string.Empty,
            MoveInType = FindTextRecursively(root, "moveInTypeName", "moveInTypeCode") ?? string.Empty,
            MoveInPossibleYmd = FindTextRecursively(root, "moveInPossibleYmd") ?? string.Empty,
            TotalParkingCount = FindTextRecursively(root, "totalParkingCount") ?? string.Empty,
            ParkingCountPerHousehold = FindTextRecursively(root, "parkingCountPerHousehold") ?? string.Empty,
            HeatingAndCoolingSystem = FindTextRecursively(root, "heatingAndCoolingSystemTypeName") ?? string.Empty,
            HeatingEnergy = FindTextRecursively(root, "heatingEnergyTypeName") ?? string.Empty,
            Options = FindJoinedValues(root, "articleOptions", "facilityList") ?? string.Empty,
            LifeFacilities = FindJoinedValues(root, "lifeFacilityList") ?? string.Empty,
            SecurityFacilities = FindJoinedValues(root, "securityFacilityList") ?? string.Empty,
            EtcFacilities = FindJoinedValues(root, "etcFacilityList") ?? string.Empty,
            BuildingUseApprovalYmd = FindTextRecursively(root, "buildingUseAprvYmd") ?? string.Empty,
            PhotoCount = FindPhotoCount(root),
            VerificationTypeCode = FindTextRecursively(root, "verificationTypeCode") ?? string.Empty,
            VerificationTypeName = FindTextRecursively(root, "verificationTypeName") ?? string.Empty,
            ArticleConfirmYmd = mergedListing.RegisteredDate,
            ExposeStartYmd = FindTextRecursively(root, "exposeStartYMD", "exposeStartYmd") ?? string.Empty,
            ExposeEndYmd = FindTextRecursively(root, "exposeEndYMD", "exposeEndYmd") ?? string.Empty,
            ArticleStatusCode = FindTextRecursively(root, "articleStatusCode") ?? string.Empty,
            ProviderId = FindTextRecursively(root, "cpId") ?? string.Empty
        };
    }

    /// <summary>
    /// 단지 광고 API(front-api/v1/realtor/advertisement) 응답에서 광고 순위순으로
    /// 중개인 정보(중개인명·중개인 ID)를 최대 maximum명 추출한다.
    /// 응답 래핑 구조와 무관하게 중개인명이 들어 있는 첫 번째 배열을 광고 목록으로 사용한다.
    /// </summary>
    public static IReadOnlyList<ComplexAdvertisementRealtor> ParseAdvertisementRealtors(string json, int maximum = 3)
    {
        if (maximum <= 0) return [];
        using var document = JsonDocument.Parse(json);
        var realtors = new List<ComplexAdvertisementRealtor>(maximum);
        CollectAdvertisementRealtors(document.RootElement, realtors, maximum);
        return realtors;
    }

    private static bool CollectAdvertisementRealtors(
        JsonElement element,
        List<ComplexAdvertisementRealtor> realtors,
        int maximum)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = GetText(item, "realtorName", "brokerName", "rltrNm") ??
                               FindTextRecursively(item, "realtorName", "brokerName", "rltrNm");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var realtorId = GetText(item, "realtorId", "brokerId", "realtorNo", "rltrId") ??
                                    FindTextRecursively(item, "realtorId", "brokerId", "realtorNo", "rltrId") ??
                                    string.Empty;
                    realtors.Add(new ComplexAdvertisementRealtor(name.Trim(), realtorId.Trim()));
                    if (realtors.Count >= maximum) return true;
                }
                if (realtors.Count > 0) return true;
                foreach (var item in element.EnumerateArray())
                    if (CollectAdvertisementRealtors(item, realtors, maximum)) return true;
                return false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (CollectAdvertisementRealtors(property.Value, realtors, maximum)) return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 단지 정보 API(/api/complexes/{complexNo}) 응답을 표시용 단지 정보로 변환한다.
    /// 응답이 complexDetail로 래핑돼 있으면 우선 사용하고, 필드가 없으면 응답 전체를 재귀 탐색한다.
    /// </summary>
    public static ComplexInformation ParseComplexInformation(string json, string complexNo, string fallbackName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var detail = root;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(root, "complexDetail", out var complexDetail) &&
            complexDetail.ValueKind == JsonValueKind.Object)
            detail = complexDetail;

        string? Find(params string[] names) => GetText(detail, names) ?? FindTextRecursively(root, names);

        return new ComplexInformation(
            complexNo,
            FirstNotEmpty(Find("complexName", "complexTypeName"), fallbackName))
        {
            HouseholdSummary = BuildHouseholdSummary(
                Find("totalHouseholdCount", "householdCount"),
                Find("totalDongCount", "dongCount")),
            FloorRange = BuildFloorRange(Find("lowFloor", "minFloor"), Find("highFloor", "maxFloor")),
            UseApproveDate = FormatKoreanDate(Find("useApproveYmd", "approveYmd", "useApprovalYmd")),
            ParkingSummary = BuildParkingSummary(
                Find("parkingPossibleCount", "totalParkingCount", "parkingCount"),
                Find("parkingCountByHousehold", "parkingCountPerHousehold")),
            FloorAreaRatio = AppendPercent(Find("batlRatio", "floorAreaRatio")),
            BuildingCoverageRatio = AppendPercent(Find("btlRatio", "buildingCoverageRatio")),
            ConstructionCompany = Find("constructionCompanyName", "constructionCompany") ?? string.Empty,
            Heating = BuildHeating(
                Find("heatMethodTypeName", "heatMethodTypeCode"),
                Find("heatFuelTypeName", "heatFuelTypeCode")),
            ManagementOfficeTel =
                Find("managementOfficeTelNo", "managementOfficeTel", "managementOfficeTelephone") ?? string.Empty,
            Address = JoinDistinct(" ", Find("address") ?? string.Empty, Find("detailAddress") ?? string.Empty),
            RoadAddress = JoinDistinct(
                " ",
                Find("roadAddressPrefix") ?? string.Empty,
                Find("roadAddress") ?? string.Empty),
            AreaNames = FindJoinedValues(root, "pyoengNames", "areaNames") ?? string.Empty
        };
    }

    private static string BuildHouseholdSummary(string? householdCount, string? dongCount)
    {
        if (string.IsNullOrWhiteSpace(householdCount)) return string.Empty;
        var summary = $"{householdCount.Trim()}세대";
        return string.IsNullOrWhiteSpace(dongCount) ? summary : $"{summary} (총{dongCount.Trim()}개동)";
    }

    private static string BuildFloorRange(string? lowFloor, string? highFloor)
    {
        var low = lowFloor?.Trim() ?? string.Empty;
        var high = highFloor?.Trim() ?? string.Empty;
        if (low.Length == 0 && high.Length == 0) return string.Empty;
        if (low.Length == 0) return $"{high}층";
        if (high.Length == 0) return $"{low}층";
        return $"{low}층/{high}층";
    }

    private static string BuildParkingSummary(string? parkingCount, string? perHousehold)
    {
        if (string.IsNullOrWhiteSpace(parkingCount)) return string.Empty;
        var summary = $"{parkingCount.Trim()}대";
        return string.IsNullOrWhiteSpace(perHousehold)
            ? summary
            : $"{summary}(세대당 {perHousehold.Trim()}대)";
    }

    private static string AppendPercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return text.EndsWith('%') ? text : $"{text}%";
    }

    private static string FormatKoreanDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return DateTime.TryParseExact(
            text,
            ["yyyyMMdd", "yyyy-MM-dd", "yyyy.MM.dd"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? $"{date:yyyy년 MM월 dd일}"
            : text;
    }

    private static string BuildHeating(string? method, string? fuel)
    {
        var parts = new[] { TranslateHeating(method), TranslateHeating(fuel) }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.CurrentCulture)
            .ToList();
        return string.Join(", ", parts);
    }

    private static string TranslateHeating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        // HT005/HF002는 실제 단지 응답(개별난방·도시가스 단지)에서 확인된 코드다.
        // 확인되지 않은 코드는 원문 그대로 표시해 잘못된 라벨을 붙이지 않는다.
        return value.Trim() switch
        {
            "individual" or "HT005" => "개별난방",
            "central" => "중앙난방",
            "district" => "지역난방",
            "gas" or "cityGas" or "HF002" => "도시가스",
            "oil" => "기름",
            "electric" => "전기",
            "nightElectric" => "심야전기",
            var text => text
        };
    }

    private static JsonElement? FindArticleArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "articleList", "articles", "list" })
        {
            if (TryGetPropertyIgnoreCase(root, name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        if (TryGetPropertyIgnoreCase(root, "result", out var result))
        {
            if (result.ValueKind == JsonValueKind.Array) return result;
            if (result.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "articleList", "articles", "list" })
                {
                    if (TryGetPropertyIgnoreCase(result, name, out var value) && value.ValueKind == JsonValueKind.Array)
                        return value;
                }
            }
        }

        return null;
    }

    private static Listing ParseListing(JsonElement item, ISet<string>? ownArticleNumbers)
    {
        var articleNo = GetText(item, "articleNo", "articleNumber", "atclNo", "id") ?? string.Empty;
        var articleName = GetText(item, "articleName", "complexName", "atclNm") ?? string.Empty;
        var realEstateType = GetText(item, "realEstateTypeName", "rletTpNm") ?? string.Empty;
        var buildingName = GetText(item, "buildingName", "building", "dongName", "bildNm") ?? string.Empty;
        var location = GetText(
            item,
            "exposeAddress",
            "cortarAddress",
            "cortarAddr",
            "roadAddress",
            "roadAddressName",
            "address",
            "location") ?? string.Empty;
        var registeredName = GetText(
            item,
            "articleFeatureDesc",
            "atclFetrDesc",
            "articleTitle",
            "articleSubject",
            "listingName",
            "articleDescription",
            "description") ?? string.Empty;
        var floorInfo = GetText(item, "floorInfo", "flrInfo", "flrNm") ?? string.Empty;
        var displayName = string.IsNullOrWhiteSpace(articleName) ? realEstateType : articleName;
        var floorDisplay = string.IsNullOrWhiteSpace(floorInfo) ? string.Empty : $"{floorInfo}층";
        var address = JoinDistinct(" · ", location, displayName, buildingName, floorDisplay);

        var dealPrice = GetText(item, "dealOrWarrantPrc", "dealOrWrterPrc", "price", "priceText", "prcInfo") ?? string.Empty;
        var rentPrice = GetText(item, "rentPrc", "monthlyRent") ?? string.Empty;
        var price = string.IsNullOrWhiteSpace(rentPrice) ? dealPrice : $"{dealPrice}/{rentPrice}";
        var area = GetText(item, "areaName", "area2", "exclusiveArea", "spc2") ?? string.Empty;

        return new Listing(
            articleNo,
            address,
            GetText(item, "tradeTypeName", "tradeType", "tradeTypeCode", "tradTpNm") ?? string.Empty,
            price,
            GetText(item, "realtorName", "brokerName", "rltrNm") ?? string.Empty,
            GetText(item, "realtorId", "brokerId", "realtorNo", "rltrId") ?? string.Empty,
            GetText(item, "cpName", "providerName") ?? string.Empty,
            buildingName,
            floorInfo,
            area,
            ownArticleNumbers?.Contains(articleNo) == true)
        {
            // 최상위 complexNo/complexNumber를 우선 사용하고, 없으면 항목 내부의 중첩 필드와
            // /complexes/{단지번호} 형태의 URL 값까지 탐색해 단지번호를 동기화한다.
            ComplexNo = GetText(item, "complexNo", "complexNumber") ??
                        FindTextRecursively(item, "complexNo", "complexNumber", "hscpNo", "complexes") ??
                        FindComplexNumberFromUrls(item) ?? string.Empty,
            ArticleName = articleName,
            RealEstateType = realEstateType,
            Location = location,
            Description = registeredName,
            RegisteredDate = GetText(
                item,
                "articleConfirmYmd",
                "articleConfirmDate",
                "confirmYmd",
                "articleRegisterYmd",
                "registerYmd",
                "registeredDate",
                "regDate") ?? string.Empty,
            VerificationTypeCode = GetText(item, "verificationTypeCode") ?? string.Empty,
            SameAddressCount = GetInt(item, "sameAddrCnt", "sameAddressCount") ?? 0
        };
    }

    private static string JoinDistinct(string separator, params string[] values) =>
        string.Join(separator, values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

    private static string? GetText(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? FindTextRecursively(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var direct = GetText(element, names);
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindTextRecursively(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindTextRecursively(item, names);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    // 항목 안의 문자열 값(예: 매물 링크 URL)에 /complexes/{단지번호} 패턴이 있으면 단지번호로 사용한다.
    private static string? FindComplexNumberFromUrls(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var match = ComplexUrlPattern.Match(element.GetString() ?? string.Empty);
                return match.Success ? match.Groups["no"].Value : null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nested = FindComplexNumberFromUrls(property.Value);
                    if (nested is not null) return nested;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindComplexNumberFromUrls(item);
                    if (nested is not null) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    private static int? FindIntRecursively(JsonElement element, params string[] names)
    {
        var value = FindTextRecursively(element, names);
        return int.TryParse(value, out var result) ? result : null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        var value = GetText(element, names);
        return int.TryParse(value, out var result) ? result : null;
    }

    private static string? FindJoinedValues(JsonElement root, params string[] names)
    {
        if (!TryFindPropertyRecursively(root, names, out var value)) return null;
        var values = new List<string>();
        CollectDisplayValues(value, values);
        var result = values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .ToList();
        return result.Count == 0 ? null : string.Join(", ", result);
    }

    private static int? FindPhotoCount(JsonElement root)
    {
        var text = FindTextRecursively(root, "siteImageCount", "photoCount");
        if (int.TryParse(text, out var count)) return count;
        if (!TryFindPropertyRecursively(root, ["articlePhotos", "photoList"], out var photos)) return null;
        return photos.ValueKind == JsonValueKind.Array ? photos.GetArrayLength() : null;
    }

    private static bool TryFindPropertyRecursively(JsonElement element, string[] names, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
                if (TryGetPropertyIgnoreCase(element, name, out value)) return true;
            foreach (var property in element.EnumerateObject())
                if (TryFindPropertyRecursively(property.Value, names, out value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindPropertyRecursively(item, names, out value)) return true;
        }
        value = default;
        return false;
    }

    private static void CollectDisplayValues(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                values.Add(element.ToString());
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectDisplayValues(item, values);
                return;
            case JsonValueKind.Object:
                var display = GetText(
                    element,
                    "name",
                    "itemName",
                    "optionName",
                    "facilityName",
                    "tagName",
                    "value");
                if (!string.IsNullOrWhiteSpace(display))
                {
                    values.Add(display);
                    return;
                }
                foreach (var property in element.EnumerateObject())
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        CollectDisplayValues(property.Value, values);
                return;
        }
    }

    private static string FloorPart(string floorInfo, int index)
    {
        if (string.IsNullOrWhiteSpace(floorInfo)) return string.Empty;
        var parts = floorInfo.Split('/', StringSplitOptions.TrimEntries);
        return index < parts.Length ? parts[index] : string.Empty;
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool? FindBoolean(JsonElement root, params string[] names)
    {
        foreach (var container in EnumerateContainers(root))
        {
            foreach (var name in names)
            {
                if (!TryGetPropertyIgnoreCase(container, name, out var value)) continue;
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                if (value.ValueKind == JsonValueKind.String)
                    return string.Equals(value.GetString(), "Y", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateContainers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        yield return root;
        if (TryGetPropertyIgnoreCase(root, "result", out var result) && result.ValueKind == JsonValueKind.Object)
            yield return result;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }
}
