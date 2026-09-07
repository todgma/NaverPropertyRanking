namespace NaverPropertyRanking.Models;

/// <summary>
/// 매물을 올리는 CP(부동산 정보제공사) 사이트 정의.
/// 계정설정 드롭다운 항목이자 접속 테스트가 열 주소를 담는다.
///
/// 아실처럼 아이디가 주소에 들어가는 CP가 있어 <see cref="LoginUrl"/>은 틀이다.
/// 틀에 {site}가 들어 있으면 계정 아이디를 끼워 넣어 완성한다.
/// </summary>
public sealed record CpSite(string Value, string Name, string LoginUrl, string SiteKeyLabel = "")
{
    /// <summary>주소 틀에서 계정별 값이 들어갈 자리.</summary>
    public const string SiteKeyToken = "{site}";

    /// <summary>지원하는 CP 목록. 새 CP는 여기에 추가하면 드롭다운과 접속 테스트에 함께 반영된다.</summary>
    public static IReadOnlyList<CpSite> All { get; } =
    [
        new("1", "부동산포스", "https://new.rfine.kr/Pos/login.php"),
        new("2", "부동산뱅크",
            "https://www.neonet.co.kr/novo-rebank/view/member/MemberLogin.neo" +
            "?login_check=yes&return_url=/novo-rebank/index.neo"),
        new("3", "이실장", "https://www.aipartner.com/integrated/login?serviceCode=1000"),
        // 아실은 아이디가 곧 주소다. 예: 02-536-6700 → https://02-536-6700.asil.kr/...
        // 로그인 화면에는 비밀번호 입력란만 있다.
        new("4", "아실",
            $"https://{SiteKeyToken}.asil.kr/member_adm/login/login.jsp",
            "아이디(접속주소)"),
        new("5", "선방", "http://homesdid.co.kr/mmc/member/login.asp"),
        new("6", "우리집 부동산", "https://woori-house.co.kr/new_mmc/member/login.asp")
    ];

    /// <summary>주소에 계정이 들어가는 CP인지. 이 경우 아이디가 곧 주소가 된다.</summary>
    public bool RequiresSiteKey => LoginUrl.Contains(SiteKeyToken, StringComparison.Ordinal);

    /// <summary>주소 틀이 있는지. 접속 테스트를 붙여 둔 CP인지 판단할 때 쓴다.</summary>
    public bool CanTestLogin => !string.IsNullOrWhiteSpace(LoginUrl);

    /// <summary>
    /// 계정 아이디를 끼워 넣어 실제 로그인 주소를 만든다.
    /// 아이디가 비어 있으면 빈 문자열을 돌려준다.
    /// </summary>
    public string ResolveLoginUrl(string? siteKey)
    {
        if (!RequiresSiteKey) return LoginUrl;

        var key = NormalizeSiteKey(siteKey);
        return key.Length == 0 ? string.Empty : LoginUrl.Replace(SiteKeyToken, key, StringComparison.Ordinal);
    }

    /// <summary>
    /// 아이디 칸에 전체 주소를 붙여 넣어도 받아들인다.
    /// "https://02-536-6700.asil.kr/..."에서 앞부분만 남겨 쓴다.
    /// </summary>
    public static string NormalizeSiteKey(string? siteKey)
    {
        var text = (siteKey ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)) text = absolute.Host;
        // 남은 경로나 슬래시를 떼고 첫 마디만 쓴다.
        text = text.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var head = text.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return head.Trim();
    }

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

    /// <summary>CP 이름. 저장할 값이 아니라 CP 목록에서 그때그때 찾는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CpName => CpSite.NameOf(CpValue);

    /// <summary>
    /// 이 계정으로 실제로 열 로그인 주소.
    /// 아실처럼 주소에 계정이 들어가는 CP는 아이디를 그대로 주소에 끼워 넣는다.
    /// 규칙이 바뀌면 즉시 반영되도록 파일에는 남기지 않는다.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LoginUrl => CpSite.Find(CpValue)?.ResolveLoginUrl(UserId) ?? string.Empty;
}
