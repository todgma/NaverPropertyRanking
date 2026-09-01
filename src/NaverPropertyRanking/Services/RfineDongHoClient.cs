using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.Services;

/// <summary>
/// 부동산포스에서 매물번호로 동·호를 읽어 온다.
/// 화면의 매물번호 검색란(#Nids)이 호출하는 목록 API를 그대로 쓰므로 HTML을 긁지 않는다.
/// 로그인은 한 번만 하고 쿠키를 유지한 채 매물별로 조회한다.
/// </summary>
public sealed class RfineDongHoClient : IDongHoLookup
{
    private const string ListApiUrl = "https://new.rfine.kr/Pos/functions/NList.php";

    /// <summary>
    /// 목록 API가 요구하는 기본 조건. 화면에서 보낸 요청과 같은 값이며
    /// 매물번호(Nids)만 매물마다 바꿔 넣는다. state는 비워 전체 상태를 검색한다.
    /// </summary>
    private const string BaseQuery =
        "Code=NList&page=1&prt=all&orderVal=1&rowsPerPageRegi=20&Article=&SType=7&SValue=" +
        "&fvo=false&subID=0&layoutnum=0&state=&UCode=&Danji_PK_ID=&DongNo=&HoNo=" +
        "&Location1=&Location2=&addType=normal&Vtype=&TradeClass=all&rettCls=&PinNo=" +
        "&rgd=&rgd1=&rgd2=&sadr=&rLType=1&fuid=&fstartd=&fendd=&fTreat=";

    /// <summary>목록 API가 함께 요구하는 필터 조건. 화면에서 조건을 걸지 않은 상태 그대로다.</summary>
    private const string EmptyFilters =
        "{\"price\":{\"sale\":null,\"rent\":null}," +
        "\"area\":{\"range\":null,\"exclusive\":false}," +
        "\"approvalDate\":{\"range\":null},\"houseCount\":{\"range\":null}," +
        "\"floor\":{\"range\":null},\"maintenanceFee\":{\"range\":null}," +
        "\"keyMoney\":{\"range\":null},\"location\":[]," +
        "\"count\":{\"room\":[],\"restRoom\":[]}," +
        "\"contract\":{\"loanAmount\":[],\"moveInDate\":[]}," +
        "\"facility\":{\"buildingFacility\":[],\"innerFacility\":[],\"roomStructure\":[]}," +
        "\"verification\":[]}";

    private readonly CpAccount _account;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private bool _loggedIn;
    private bool _disposed;

    public string CpName => "부동산포스";

    public RfineDongHoClient(CpAccount account, HttpMessageHandler? handler = null)
    {
        _account = account;
        handler ??= new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36");
    }

    /// <summary>로그인은 한 번만 한다. 실패하면 이후 조회를 시도하지 않는다.</summary>
    public async Task<CpLoginTestResult> EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loggedIn) return CpLoginTestResult.Ok("이미 로그인되어 있습니다.");
            var site = CpSite.Find(_account.CpValue);
            if (site is null) return CpLoginTestResult.Fail($"지원하지 않는 CP입니다: {_account.CpValue}");

            var result = await CpLoginTester
                .TestWithClientAsync(_client, site, _account, cancellationToken)
                .ConfigureAwait(false);
            _loggedIn = result.Success;
            return result;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// 매물번호로 동·호를 조회한다. 찾지 못하면 빈 값을 돌려주고 예외를 던지지 않는다.
    /// 한 건이 실패해도 나머지 매물 조회가 멈추지 않게 하기 위해서다.
    /// </summary>
    public async Task<DongHo> GetDongHoAsync(string articleNo, CancellationToken cancellationToken)
    {
        if (!_loggedIn || string.IsNullOrWhiteSpace(articleNo)) return DongHo.Empty;

        try
        {
            var url = $"{ListApiUrl}?{BaseQuery}" +
                      $"&Nids={Uri.EscapeDataString(articleNo.Trim())}" +
                      $"&filters={Uri.EscapeDataString(EmptyFilters)}";
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return DongHo.Empty;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // 로그인이 풀리면 JSON 대신 로그인 안내가 돌아온다.
            if (body.Contains("로그인이 필요합니다", StringComparison.Ordinal))
            {
                _loggedIn = false;
                return DongHo.Empty;
            }
            return ParseDongHo(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DongHo.Empty;
        }
    }

    /// <summary>
    /// 목록 API 응답에서 첫 매물의 상세주소(FAddr2)를 찾아 동·호를 읽는다.
    /// 매물번호로 검색하므로 결과는 한 건이다.
    /// </summary>
    public static DongHo ParseDongHo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
                return DongHo.Empty;

            var address = data[0].TryGetProperty("FAddr2", out var addressElement)
                ? addressElement.GetString() ?? string.Empty
                : string.Empty;
            return ParseAddress(address);
        }
        catch (JsonException)
        {
            return DongHo.Empty;
        }
    }

    /// <summary>
    /// 상세주소 문자열에서 동·호를 뽑는다.
    /// 예: "꽃메마을극동스타클래스 201동 402호" → 201동 / 402호.
    /// 단지명에 '동'이 붙어 있어도 공백으로 끊긴 낱말만 보므로 섞이지 않는다.
    /// </summary>
    public static DongHo ParseAddress(string? address) => DongHoParser.ParseAddress(address);

    /// <summary>동·호를 가질 수 있는 매물유형인지. 공통 규칙을 그대로 쓴다.</summary>
    public static bool SupportsDongHo(string? realEstateType) =>
        DongHoParser.SupportsDongHo(realEstateType);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loginGate.Dispose();
        _client.Dispose();
    }
}
