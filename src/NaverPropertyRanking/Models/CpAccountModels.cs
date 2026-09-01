namespace NaverPropertyRanking.Models;

/// <summary>
/// 매물을 올리는 CP(부동산 정보제공사) 사이트 정의.
/// 계정설정 드롭다운 항목이자 접속 테스트가 열 주소를 담는다.
/// </summary>
public sealed record CpSite(string Value, string Name, string LoginUrl)
{
    /// <summary>지원하는 CP 목록. 새 CP는 여기에 추가하면 드롭다운과 접속 테스트에 함께 반영된다.</summary>
    public static IReadOnlyList<CpSite> All { get; } =
    [
        new("1", "부동산포스", "https://new.rfine.kr/Pos/login.php"),
        new("2", "부동산뱅크",
            "https://www.neonet.co.kr/novo-rebank/view/member/MemberLogin.neo" +
            "?login_check=yes&return_url=/novo-rebank/index.neo"),
        new("3", "이실장", "https://www.aipartner.com/integrated/login?serviceCode=1000")
    ];

    /// <summary>접속 테스트를 할 수 있는지. 로그인 주소가 등록돼 있어야 한다.</summary>
    public bool CanTestLogin => !string.IsNullOrWhiteSpace(LoginUrl);

    public static CpSite? Find(string? value) =>
        All.FirstOrDefault(site => string.Equals(site.Value, value, StringComparison.Ordinal));

    public static string NameOf(string? value) => Find(value)?.Name ?? value ?? string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// 저장된 CP 계정 한 건. 비밀번호는 파일에 평문으로 남기지 않는다.
/// CP 하나당 계정 하나를 유지한다.
/// </summary>
public sealed record CpAccount
{
    public string CpValue { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;

    /// <summary>DPAPI로 보호한 비밀번호. 저장한 Windows 계정에서만 복호화된다.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    public DateTime SavedAt { get; set; }

    /// <summary>파일에 쓰지 않는 평문 비밀번호. 메모리에서만 쓴다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public string CpName => CpSite.NameOf(CpValue);
}
