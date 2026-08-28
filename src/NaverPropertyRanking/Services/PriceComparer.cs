using System.Globalization;
using System.Text.RegularExpressions;

namespace NaverPropertyRanking.Services;

/// <summary>
/// "5억 3,000", "8억", "3,000/50" 같은 네이버 금액 표기를 비교한다.
/// 월세처럼 "보증금/월세" 형태면 보증금이 같을 때 월세로 비교한다.
/// </summary>
public static class PriceComparer
{
    private static readonly Regex EokPattern = new(
        @"(?<eok>[0-9]+(?:\.[0-9]+)?)\s*억",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>이전 금액 대비 현재 금액의 방향. 오르면 1, 내리면 -1, 같거나 비교 불가면 0.</summary>
    public static int Compare(string? previousPrice, string? currentPrice)
    {
        var previousParts = SplitParts(previousPrice);
        var currentParts = SplitParts(currentPrice);

        for (var index = 0; index < Math.Max(previousParts.Length, currentParts.Length); index++)
        {
            if (!TryParse(previousParts.ElementAtOrDefault(index), out var previous) ||
                !TryParse(currentParts.ElementAtOrDefault(index), out var current))
                return 0;
            if (current > previous) return 1;
            if (current < previous) return -1;
        }

        return 0;
    }

    private static string[] SplitParts(string? value) =>
        (value ?? string.Empty).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>금액 문자열을 만원 단위 숫자로 변환한다.</summary>
    public static bool TryParse(string? value, out decimal priceInTenThousands)
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
            if (!decimal.TryParse(
                    match.Groups["eok"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var eok))
                return false;
            priceInTenThousands = eok * 10_000m;

            var remainder = normalized[(match.Index + match.Length)..].Trim();
            if (remainder.Length == 0) return true;
            if (!decimal.TryParse(remainder, NumberStyles.Number, CultureInfo.InvariantCulture, out var rest))
                return false;
            priceInTenThousands += rest;
            return true;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out priceInTenThousands);
    }
}
