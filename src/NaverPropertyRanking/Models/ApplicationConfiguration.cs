namespace NaverPropertyRanking.Models;

public sealed class AppFileConfiguration
{
    public ApiConfiguration Api { get; set; } = new();
    public GoogleAuthenticationConfiguration GoogleAuthentication { get; set; } = new();
    public UpdateConfiguration Update { get; set; } = new();
}

public sealed class GoogleAuthenticationConfiguration
{
    public bool Enabled { get; set; }
    public string WebAppUrl { get; set; } = string.Empty;
    public string PublicIpEndpoint { get; set; } = "https://api.ipify.org";
    public int RequestTimeoutSeconds { get; set; } = 60;
}

public sealed class UpdateConfiguration
{
    public bool Enabled { get; set; }
    public bool CheckOnStartup { get; set; } = true;
    public string CurrentVersion { get; set; } = "1.0.0";
    public string ReleasesApiUrl { get; set; } = string.Empty;
    public string LatestReleaseApiUrl { get; set; } = string.Empty;
    public string ReleaseTagPrefix { get; set; } = string.Empty;
    public string ReleasesPageUrl { get; set; } = string.Empty;
    public string AssetName { get; set; } = "NaverPropertyRanking.exe";
}

public sealed class ApiConfiguration
{
    public string BaseUrl { get; set; } = "https://new.land.naver.com";
    public ApiEndpointConfiguration RealtorArticleList { get; set; } = new()
    {
        Endpoint = "/api/articles",
        RealtorIdParameter = "realtorId",
        Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["realEstateType"] = string.Empty,
            ["tradeType"] = string.Empty,
            ["order"] = "rank",
            ["page"] = "1",
            ["zoom"] = "0"
        }
    };
    public ApiEndpointConfiguration Ranking { get; set; } = new()
    {
        Endpoint = "/api/articles",
        Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index"] = "1"
        }
    };
    public ApiEndpointConfiguration ArticleDetail { get; set; } = new()
    {
        Endpoint = "/api/articles/{articleNo}"
    };
    public ApiEndpointConfiguration ComplexDetail { get; set; } = new()
    {
        Endpoint = "/api/complexes/{complexNo}"
    };
    /// <summary>
    /// 단지별 광고 상위 매물(중개인) 조회 API. 엔드포인트가 http(s)로 시작하면
    /// BaseUrl 대신 해당 절대 주소로 호출한다(fin.land.naver.com).
    /// </summary>
    public ApiEndpointConfiguration RealtorAdvertisement { get; set; } = new()
    {
        Endpoint = "https://fin.land.naver.com/front-api/v1/realtor/advertisement",
        Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["advertisementRerankChannelType"] = "property.complex.price",
            ["tradeTypes[]"] = "A1"
        }
    };
    public ApiEndpointConfiguration ComplexAdvertising { get; set; } = new()
    {
        Endpoint = "/api/articles/complex/{complexNo}",
        Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["realEstateType"] = "APT:ABYG:JGC:PRE",
            ["tradeType"] = string.Empty,
            ["priceType"] = "RETAIL",
            ["page"] = "1",
            ["order"] = "rank",
            ["showArticle"] = "false",
            ["sameAddressGroup"] = "false",
            ["type"] = "list"
        }
    };
}

public sealed class ApiEndpointConfiguration
{
    public string Endpoint { get; set; } = "/api/articles";
    public string RealtorIdParameter { get; set; } = "realtorId";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
