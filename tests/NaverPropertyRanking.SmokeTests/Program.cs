using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

var tests = new List<(string Name, Action Run)>
{
    ("배열 응답 파싱", ParseArrayResponse),
    ("래핑된 응답 파싱", ParseWrappedResponse),
    ("쿠키 사전 변환", NormalizeCookies),
    ("알림 변화 감지", DetectChanges),
    ("매물번호 직접 조회 우선", DirectArticleNumbersTakePriority),
    ("랭킹 체크 선택 범위", SelectRankingTargets),
    ("매물 표시 행수 선택", PaginateListings),
    ("이전·현재 랭킹 변동 표시", PresentRankMovement),
    ("단지 매물 링크 생성", BuildArticleLink),
    ("통합 알림 내용 생성", FormatConsolidatedNotification),
    ("프로그램 중복 실행 차단", BlockDuplicateApplicationInstance),
    ("구글 인증 로그인 응답 처리", HandleGoogleAuthentication),
    ("GitHub 최신 버전 확인", CheckGitHubRelease),
    ("429 쿨다운으로 재호출 차단", RateLimitCooldownBlocksRetry),
    ("누락 인증 차단 및 JWT exp 비차단", ValidateAuthentication),
    ("appsettings API 옵션 적용", ApplyApiConfiguration),
    ("단일 파일용 설정 리소스 포함", EmbeddedConfigurationAvailable)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void ParseArrayResponse()
{
    const string json = """
        [
          {"articleNo":"2600000001","complexNo":"109250","articleName":"테스트아파트","buildingName":"101동","tradeTypeName":"매매","dealOrWarrantPrc":"5억","sameAddrMinPrc":"4억 9,000","sameAddrMaxPrc":"5억 1,000","realtorName":"우리부동산"},
          {"articleNo":"2600000002","articleName":"테스트아파트","buildingName":"101동","tradeTypeName":"매매","dealOrWarrantPrc":"5억 1,000","realtorName":"다른부동산"}
        ]
        """;
    var parsed = NaverResponseParser.ParseArticleResponse(json, new HashSet<string> { "2600000001" });
    Assert(parsed.Listings.Count == 2, "목록 수");
    Assert(parsed.Listings[0].IsMine, "내 매물 식별");
    Assert(parsed.Listings[0].Address == "테스트아파트 101동", "주소 조합");
    Assert(parsed.Listings[0].ComplexNo == "109250", "단지번호 파싱");
    var range = NaverResponseParser.ParseSameAddressPrices(json);
    Assert(range.MinPrice == "4억 9,000" && range.MaxPrice == "5억 1,000", "가격 범위");
}

static void ParseWrappedResponse()
{
    const string json = """
        {"result":{"articleList":[{"articleNo":"2600000003","articleName":"래핑아파트"}],"isMoreData":true}}
        """;
    var parsed = NaverResponseParser.ParseArticleResponse(json);
    Assert(parsed.Listings.Count == 1, "래핑 목록 수");
    Assert(parsed.IsMoreData == true, "다음 페이지 여부");
}

static void NormalizeCookies()
{
    var cookie = NaverLandClient.NormalizeCookieHeader("cookies = {'NAC': 'abc', 'NNB': 'def'}");
    Assert(cookie == "NAC=abc; NNB=def", "Python 사전 변환");
}

static void DetectChanges()
{
    var mine = new Listing("2600000001", "테스트아파트 101동", "매매", "5억", "우리부동산", "mine", "", "101동", "10/20", "84", true);
    var competitor = new Listing("2600000002", "테스트아파트 101동", "매매", "5억 2,000", "다른부동산", "other", "", "101동", "10/20", "84");
    var result = new RankingResult(mine, 5, 2, "5억", "5억 2,000", [mine, competitor]);
    var previous = new ListingSnapshot(2, new Dictionary<string, string> { [competitor.ArticleNo] = "5억 1,000" }, 0, DateTime.UtcNow.AddMinutes(-10));
    var settings = new AppSettings { RankThreshold = 5 };
    var comparison = RankingAnalyzer.Compare(result, previous, settings);
    Assert(comparison.Events.Any(x => x.Title == "매물 랭킹 변경"), "랭킹 변경 알림");
    Assert(comparison.Events.Any(x => x.Title == "랭킹 기준 알림"), "기준 알림");
    Assert(comparison.Events.Any(x => x.Title == "동일매물 가격 변경"), "가격 알림");
    Assert(comparison.Events.Any(x => x.Title == "단독매물 상태 변경"), "신규 동일매물 알림");
}

static void DirectArticleNumbersTakePriority()
{
    var handler = new StubHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri?.Query.Contains("representativeArticleNo=2612345678") == true
            ? "[{\"articleNo\":\"2612345678\"}]"
            : throw new InvalidOperationException("중개인 목록 API가 호출되었습니다."))
    });
    var configuration = new ApiConfiguration
    {
        Ranking = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string> { ["index"] = "1" }
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var settings = new AppSettings
    {
        GroupId = string.Empty,
        ManualArticleNumbers = "2612345678"
    };
    var listings = client.GetOwnListingsAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
    Assert(listings.Count == 1, "직접 조회 매물 수");
    Assert(handler.CallCount == 0, "중개인 목록 호출 생략");

    var result = client.GetRankingAsync(
        listings[0],
        new HashSet<string> { listings[0].ArticleNo },
        settings,
        CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.Success && result.Rank == 1, "중개인 ID 없는 랭킹 조회");
    Assert(handler.CallCount == 1, "랭킹 API만 한 번 호출");

    var urlNumbers = NaverLandClient.ParseManualArticleNumbers(
        "https://new.land.naver.com/complexes/109250?ms=2ALvXw,3zh6a4,15&articleNo=2526973862");
    Assert(urlNumbers.SequenceEqual(new[] { "2526973862" }), "전체 URL에서 articleNo만 추출");
}

static void RateLimitCooldownBlocksRetry()
{
    var handler = new StubHandler(_ =>
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        return response;
    });
    using var client = new NaverLandClient(handler);
    var settings = new AppSettings { GroupId = "group-id" };

    try { client.GetOwnListingsAsync(settings, CancellationToken.None).GetAwaiter().GetResult(); } catch (NaverApiException) { }
    try { client.GetOwnListingsAsync(settings, CancellationToken.None).GetAwaiter().GetResult(); } catch (NaverApiException) { }

    Assert(handler.CallCount == 1, "쿨다운 중 HTTP 재호출 차단");
    Assert(settings.RateLimitBlockedUntilUtc > DateTime.UtcNow, "쿨다운 종료 시각 저장");
    Assert(settings.RateLimitCooldownSource.Contains("Retry-After"), "쿨다운 근거 저장");
}

static void SelectRankingTargets()
{
    var listings = new[]
    {
        new Listing("2600000001", "A", "", "", "", "", "", "", "", "", true),
        new Listing("2600000002", "B", "", "", "", "", "", "", "", "", true),
        new Listing("2600000003", "C", "", "", "", "", "", "", "", "", true)
    };

    Assert(RankingTargetSelector.Select(listings, new HashSet<string>()).Count == 3, "미선택은 전체");
    var partial = RankingTargetSelector.Select(listings, new HashSet<string> { "2600000002" });
    Assert(partial.Count == 1 && partial[0].ArticleNo == "2600000002", "일부 선택은 선택 항목만");
    Assert(RankingTargetSelector.Select(
        listings,
        new HashSet<string>(listings.Select(listing => listing.ArticleNo))).Count == 3, "전체 선택은 전체");

    var selected = new HashSet<string> { "2600000002" };
    Assert(RankingTargetSelector.ShouldRefreshOnClose(
        listings, selected, new HashSet<string>(), null, DateTime.UtcNow), "미조회 선택은 닫을 때 조회");
    Assert(!RankingTargetSelector.ShouldRefreshOnClose(
        listings, selected, selected, DateTime.UtcNow, DateTime.UtcNow), "방금 조회한 동일 선택은 중복 방지");
    Assert(RankingTargetSelector.ShouldRefreshOnClose(
        listings, selected, selected, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow), "이전 조회는 다시 조회");
}

static void PresentRankMovement()
{
    Assert(RankPresentation.FormatPrevious(5) == "5위", "이전 랭킹 표시");
    Assert(RankPresentation.GetMovement(5, 3) == RankMovement.Up, "랭킹 상승 판정");
    Assert(RankPresentation.FormatCurrent(5, 3) == "3위 ↑2", "랭킹 상승 화살표");
    Assert(RankPresentation.GetMovement(3, 6) == RankMovement.Down, "랭킹 하락 판정");
    Assert(RankPresentation.FormatCurrent(3, 6) == "6위 ↓3", "랭킹 하락 화살표");
    Assert(RankPresentation.FormatCurrent(3, 3) == "3위", "랭킹 유지 표시");
}

static void BuildArticleLink()
{
    var listing = new Listing("2526973862", "테스트", "", "", "", "", "", "", "", "")
    {
        ComplexNo = "109250"
    };
    Assert(
        NaverArticleLinkBuilder.Build(listing) ==
        "https://new.land.naver.com/complexes/109250?articleNo=2526973862",
        "complexNo 기반 링크");

    var withoutComplex = listing with { ComplexNo = string.Empty };
    Assert(
        NaverArticleLinkBuilder.Build(withoutComplex) ==
        "https://new.land.naver.com/?articleNo=2526973862",
        "단지번호 없는 매물 대체 링크");
}

static void FormatConsolidatedNotification()
{
    var text = RankingNotificationFormatter.Format(
    [
        new NotificationEvent("동일매물 가격 변경", "A부동산: 5억 → 4억 9,000"),
        new NotificationEvent("매물 랭킹 변경", "테스트아파트: 5위 → 3위"),
        new NotificationEvent("단독매물 상태 변경", "동일매물 1건 신규")
    ]);
    Assert(text.Contains("[매물 랭킹 변경] 1건"), "랭킹 알림 묶음");
    Assert(text.Contains("[동일매물 가격 변경] 1건"), "가격 알림 묶음");
    Assert(text.Contains("[단독매물 상태 변경] 1건"), "동일매물 알림 묶음");
    Assert(RankingNotificationFormatter.Format([]) == "변동 내역이 없습니다.", "변동 없음 문구");
}

static void BlockDuplicateApplicationInstance()
{
    var mutexName = $@"Local\NaverPropertyRanking.Test.{Guid.NewGuid():N}";
    Assert(SingleInstanceGuard.TryAcquire(mutexName, out var first), "첫 실행 잠금 획득");
    using (first)
    {
        Assert(!SingleInstanceGuard.TryAcquire(mutexName, out var second), "두 번째 실행 차단");
        second.Dispose();
    }

    Assert(SingleInstanceGuard.TryAcquire(mutexName, out var afterRelease), "종료 후 다시 실행");
    afterRelease.Dispose();
}

static void HandleGoogleAuthentication()
{
    var handler = new StubHandler(request =>
    {
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("203.0.113.10") };

        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        if (body.Contains("\"action\":\"heartbeat\""))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"code\":\"HEARTBEAT_OK\",\"message\":\"접속 상태가 갱신되었습니다.\"}")
            };
        if (body.Contains("\"action\":\"logout\""))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"code\":\"LOGOUT_SUCCESS\",\"message\":\"로그아웃되었습니다.\"}")
            };
        Assert(body.Contains("\"action\":\"login\""), "로그인 action 전송");
        Assert(body.Contains("\"deviceId\":"), "PC 식별자 전송");
        Assert(body.Contains("203.0.113.10"), "공인 IP 전송");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"success":true,"code":"LOGIN_SUCCESS","message":"로그인되었습니다.","userId":"testuser","name":"홍길동","token":"device-token","sessionId":"session-id","membershipStart":"2026-08-01T00:00:00+09:00","membershipEnd":"2026-09-01T00:00:00+09:00","allowedPcCount":2,"currentPcCount":1}
                """)
        };
    });
    var configuration = new GoogleAuthenticationConfiguration
    {
        Enabled = true,
        WebAppUrl = "https://example.test/auth",
        PublicIpEndpoint = "https://example.test/ip"
    };
    using var client = new GoogleAuthenticationClient(configuration, handler);
    var result = client.LoginAsync("testuser", "password123", CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(result.Success && result.Session is not null, "로그인 성공 응답");
    Assert(result.Session!.Token == "device-token", "로그인 토큰");
    Assert(result.Session.SessionId == "session-id", "로그인 세션 ID");
    Assert(result.Session.AllowedPcCount == 2 && result.Session.CurrentPcCount == 1, "PC 수 응답");
    var heartbeat = client.HeartbeatAsync(result.Session, CancellationToken.None).GetAwaiter().GetResult();
    Assert(heartbeat.Success && heartbeat.Code == "HEARTBEAT_OK", "heartbeat 갱신");
    var logout = client.LogoutAsync(result.Session, CancellationToken.None).GetAwaiter().GetResult();
    Assert(logout.Success && logout.Code == "LOGOUT_SUCCESS", "정상 로그아웃");

    var pcLimitHandler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"success\":false,\"code\":\"PC_LIMIT\",\"message\":\"사용 가능한 PC 수를 초과했습니다.\"}")
    });
    using var pcLimitClient = new GoogleAuthenticationClient(configuration, pcLimitHandler);
    var pcLimit = pcLimitClient.HeartbeatAsync(result.Session, CancellationToken.None).GetAwaiter().GetResult();
    Assert(!pcLimit.Success && pcLimit.Code == "PC_LIMIT", "heartbeat PC 제한 응답");

    var unauthorizedHandler = new StubHandler(request => request.Method == HttpMethod.Get
        ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("203.0.113.10") }
        : new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    using var unauthorizedClient = new GoogleAuthenticationClient(configuration, unauthorizedHandler);
    var unauthorized = unauthorizedClient.LoginAsync("testuser", "password123", CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(!unauthorized.Success && unauthorized.Message.Contains("접근이 거부"), "401 배포 권한 안내");
}

static void CheckGitHubRelease()
{
    var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"tag_name":"v1.2.0","html_url":"https://github.com/example/repo/releases/tag/v1.2.0","assets":[{"name":"NaverPropertyRanking.zip","browser_download_url":"https://github.com/example/repo/releases/download/v1.2.0/NaverPropertyRanking.zip"}]}
            """)
    });
    using var service = new GitHubUpdateService(new UpdateConfiguration
    {
        Enabled = true,
        CheckOnStartup = true,
        CurrentVersion = "1.1.0",
        LatestReleaseApiUrl = "https://api.github.com/repos/example/repo/releases/latest",
        AssetName = "NaverPropertyRanking.zip"
    }, handler);
    var result = service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.UpdateAvailable, "새 버전 판정");
    Assert(result.LatestVersion == "1.2.0", "최신 태그 파싱");
    Assert(result.DownloadUrl.EndsWith("NaverPropertyRanking.zip"), "릴리스 자산 URL");
}

static void PaginateListings()
{
    var listings = Enumerable.Range(1, 35)
        .Select(index => new Listing($"260000{index:D4}", $"매물 {index}", "", "", "", "", "", "", "", "", true))
        .ToList();

    Assert(ListingPagination.GetPageCount(listings.Count, 10) == 4, "10건 페이지 수");
    Assert(ListingPagination.GetPage(listings, 2, 10).Count == 10, "10건 두 번째 페이지");
    Assert(ListingPagination.GetPageCount(listings.Count, 20) == 2, "20건 페이지 수");
    Assert(ListingPagination.GetPageCount(listings.Count, 30) == 2, "30건 페이지 수");
    Assert(ListingPagination.GetPage(listings, 1, 0).Count == 35, "전체 표시");

    var defaults = new AppSettings();
    Assert(defaults.DisplayPageSize == 0, "기본 표시 전체");
    Assert(defaults.RankImmediatelyAfterListingLoad, "기본 랭킹 바로조회 체크");
}

static void ValidateAuthentication()
{
    var missing = NaverAuthValidator.GetError(new AppSettings());
    Assert(missing?.Contains("인증값이 없습니다") == true, "누락 인증 감지");

    var expiredPayload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"exp\":1}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var jwtWithPastExp = NaverAuthValidator.GetError(new AppSettings
    {
        BearerToken = $"x.{expiredPayload}.x",
        CookieHeader = "NNB=test"
    });
    Assert(jwtWithPastExp is null, "JWT exp만으로 실제 요청을 사전 차단하지 않음");
}

static void ApplyApiConfiguration()
{
    var handler = new StubHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri?.Query.Contains("representativeArticleNo") == true
            ? "[{\"articleNo\":\"2612345678\"}]"
            : "{\"articleList\":[],\"isMoreData\":false}")
    });
    var configuration = new ApiConfiguration
    {
        BaseUrl = "https://new.land.naver.com",
        RealtorArticleList = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            RealtorIdParameter = "realtorId",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer list-token",
                ["Cookie"] = "LIST=test"
            },
            Params = new Dictionary<string, string>
            {
                ["realEstateType"] = string.Empty,
                ["tradeType"] = string.Empty,
                ["order"] = "rank",
                ["zoom"] = "0"
            }
        },
        Ranking = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer ranking-token",
                ["Cookie"] = "RANK=test"
            },
            Params = new Dictionary<string, string> { ["index"] = "1" }
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var settings = new AppSettings
    {
        GroupId = "bizmk",
    };
    client.GetOwnListingsAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

    Assert(handler.LastRequestUri?.Query.Contains("realtorId=bizmk") == true, "realtorId 쿼리");
    Assert(handler.LastRequestUri?.Query.Contains("order=rank") == true, "목록 파라미터");
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "list-token", "목록 Authorization 적용");
    Assert(handler.CookieHeader == "LIST=test", "목록 Cookie 적용");

    var own = new Listing("2612345678", "직접", "", "", "", "", "", "", "", "", true);
    client.GetRankingAsync(own, new HashSet<string> { own.ArticleNo }, settings, CancellationToken.None).GetAwaiter().GetResult();
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "ranking-token", "랭킹 Authorization 분리");
    Assert(handler.CookieHeader == "RANK=test", "랭킹 Cookie 분리");
}

static void EmbeddedConfigurationAvailable()
{
    var assembly = typeof(ApplicationConfigurationLoader).Assembly;
    Assert(
        assembly.GetManifestResourceNames().Contains("NaverPropertyRanking.appsettings.json"),
        "appsettings 내장 리소스");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }
    public string? CookieHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        CookieHeader = request.Headers.TryGetValues("Cookie", out var values) ? values.SingleOrDefault() : null;
        return Task.FromResult(responder(request));
    }
}
