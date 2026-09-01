using System.Net;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>매물 하나에서 읽어 온 동·호.</summary>
public sealed record DongHo(string Dong, string Ho)
{
    public static DongHo Empty { get; } = new(string.Empty, string.Empty);
    public bool HasValue => Dong.Length > 0 || Ho.Length > 0;
}

/// <summary>
/// CP 한 곳에서 매물번호로 동·호를 읽어 오는 통로.
/// CP마다 로그인 방식과 조회 주소가 달라 구현을 따로 두고, 호출하는 쪽은 이 통로만 본다.
/// </summary>
public interface IDongHoLookup : IDisposable
{
    /// <summary>이 통로가 어느 CP를 보는지. 상태 메시지에 쓴다.</summary>
    string CpName { get; }

    /// <summary>로그인은 한 번만 한다. 실패하면 이후 조회를 시도하지 않는다.</summary>
    Task<CpLoginTestResult> EnsureLoggedInAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 매물번호로 동·호를 조회한다. 찾지 못하면 빈 값을 돌려주고 예외를 던지지 않는다.
    /// 한 건이 실패해도 나머지 매물 조회가 멈추지 않게 하기 위해서다.
    /// </summary>
    Task<DongHo> GetDongHoAsync(string articleNo, CancellationToken cancellationToken);
}

/// <summary>
/// CP가 어떤 형태로 주소를 주든 똑같이 적용되는 동·호 판단 규칙.
/// </summary>
public static class DongHoParser
{
    /// <summary>
    /// 동·호 개념이 없는 매물유형. 토지·단독주택처럼 건물이 통째이거나 땅인 매물이다.
    /// 이런 매물은 조회해도 항상 빈 값이라 아예 묻지 않는다.
    /// </summary>
    private static readonly string[] TypesWithoutDongHo =
    [
        "토지", "임야", "전답", "과수원", "대지",
        "단독", "다가구", "상가", "사무실", "공장", "창고", "빌딩", "건물"
    ];

    /// <summary>
    /// 건물의 동·호는 '103동'처럼 숫자가 앞에 붙거나 'A동'처럼 영문 한 글자가 붙는다.
    /// 숫자·영문을 요구하므로 법정동(죽전동)이나 단지명(가능동)은 애초에 걸리지 않는다.
    /// '512동502호'처럼 동과 호가 붙어 있는 표기도 있어 뒤에 공백을 요구하지 않는다.
    /// </summary>
    private static readonly Regex DongPattern = new(
        @"(?:제)?(?<value>(?:[0-9]+(?:-[0-9]+)?[가-힣A-Za-z]?|[A-Za-z])동)",
        RegexOptions.Compiled);
    private static readonly Regex HoPattern = new(
        @"(?:제)?(?<value>(?:[0-9]+(?:-[0-9]+)?[가-힣A-Za-z]?|[A-Za-z])호)",
        RegexOptions.Compiled);

    private static readonly Regex TagPattern = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex SpacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 주소 문자열에서 동·호를 뽑는다.
    /// 예: "방배아트자이 103동 602호" → 103동 / 602호.
    /// </summary>
    public static DongHo ParseAddress(string? address)
    {
        var text = Normalize(address);
        if (text.Length == 0) return DongHo.Empty;
        return new DongHo(LastMatch(DongPattern, text), LastMatch(HoPattern, text));
    }

    /// <summary>태그를 걷어내고 공백을 하나로 줄인다. HTML로 오는 CP에 쓴다.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var plain = WebUtility.HtmlDecode(TagPattern.Replace(text, " "));
        return SpacePattern.Replace(plain, " ").Trim();
    }

    /// <summary>주소 앞쪽 법정동(예: 죽전동)이 아니라 뒤쪽 동을 쓰도록 마지막 일치를 택한다.</summary>
    private static string LastMatch(Regex pattern, string text)
    {
        var matches = pattern.Matches(text);
        return matches.Count == 0 ? string.Empty : matches[^1].Groups["value"].Value;
    }

    /// <summary>
    /// 이 매물유형이 동·호를 가질 수 있는지 판단한다.
    /// 매물유형을 모르면 조회해 보는 쪽을 택한다(놓치는 것보다 한 번 더 묻는 편이 낫다).
    /// </summary>
    public static bool SupportsDongHo(string? realEstateType)
    {
        var type = (realEstateType ?? string.Empty).Trim();
        if (type.Length == 0) return true;
        return !TypesWithoutDongHo.Any(keyword => type.Contains(keyword, StringComparison.Ordinal));
    }
}
