using System.Text.Json;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

public static class NaverResponseParser
{
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
        var address = JoinDistinct(" · ", location, displayName, buildingName, registeredName, floorDisplay);

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
            GetText(item, "realtorId", "brokerId") ?? string.Empty,
            GetText(item, "cpName", "providerName") ?? string.Empty,
            buildingName,
            floorInfo,
            area,
            ownArticleNumbers?.Contains(articleNo) == true)
        {
            ComplexNo = GetText(item, "complexNo", "complexNumber") ?? string.Empty,
            ArticleName = articleName,
            Description = registeredName
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
