using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

var tests = new List<(string Name, Action Run)>
{
    ("배열 응답 파싱", ParseArrayResponse),
    ("래핑된 응답 파싱", ParseWrappedResponse),
    ("상가·창고 소재지와 등록 매물명 파싱", ParseCommercialListingDetails),
    ("쿠키 사전 변환", NormalizeCookies),
    ("알림 변화 감지", DetectChanges),
    ("매물번호 직접 조회 우선", DirectArticleNumbersTakePriority),
    ("매물 목록 행 단위 스트림", StreamListingsOneAtATime),
    ("반복 매물 페이지 조회 중단", StopRepeatedListingPages),
    ("로그인·단체별 매물 로컬 캐시", PersistListingCacheByLoginAndGroup),
    ("매물 랭킹·동일매물 정렬", SortListingsByRankingAndDuplicates),
    ("단일매물 제외 및 재표시", FilterSingleListings),
    ("캐시와 최신 매물 신규 항목 병합", MergeOnlyMissingListings),
    ("현재 목록과 API 결과 추가·삭제 동기화", ReconcileCurrentListings),
    ("매물목록과 순위 API 동시 실행", RunListingAndRankingConcurrently),
    ("매물 전체·조회 진행 표시", FormatListingLookupProgress),
    ("랭킹 체크 선택 범위", SelectRankingTargets),
    ("매물 표시 행수 선택", PaginateListings),
    ("이전·현재 랭킹 변동 표시", PresentRankMovement),
    ("단지 매물 링크 생성", BuildArticleLink),
    ("통합 알림 내용 생성", FormatConsolidatedNotification),
    ("변동상태별 독립 팝업 구성", PlanSeparateNotificationPopups),
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
    Assert(parsed.Listings[0].Address == "테스트아파트 · 101동", "주소 조합");
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

static void ParseCommercialListingDetails()
{
    const string json = """
        {"articleList":[{
          "articleNo":"2600000004",
          "articleName":"상가",
          "realEstateTypeName":"상가",
          "cortarAddress":"서울시 강남구 역삼동",
          "buildingName":"테스트빌딩",
          "articleFeatureDesc":"대로변 상가 1층",
          "floorInfo":"1/5",
          "tradeTypeName":"월세",
          "dealOrWarrantPrc":"5,000",
          "rentPrc":"300"
        }]}
        """;

    var listing = NaverResponseParser.ParseArticleResponse(json).Listings.Single();
    Assert(listing.Address.Contains("서울시 강남구 역삼동"), "소재지 표시");
    Assert(listing.Address.Contains("대로변 상가 1층"), "등록 매물명 표시");
    Assert(listing.Address.Contains("1/5층"), "층 정보 표시");
    Assert(listing.Price == "5,000/300", "상가 월세 표시");
}

static void NormalizeCookies()
{
    var cookie = NaverLandClient.NormalizeCookieHeader("cookies = {'NAC': 'abc', 'NNB': 'def'}");
    Assert(cookie == "NAC=abc; NNB=def", "Python 사전 변환");
}

static void DetectChanges()
{
    var mine = new Listing("2600000001", "테스트아파트 101동", "매매", "5억", "우리부동산", "mine", "", "101동", "10/20", "84", true)
    {
        ArticleName = "테스트아파트"
    };
    var competitor = new Listing("2600000002", "테스트아파트 101동", "매매", "5억 2,000", "다른부동산", "other", "", "101동", "10/20", "84");
    var result = new RankingResult(mine, 5, 2, "5억", "5억 2,000", [mine, competitor]);
    var previous = new ListingSnapshot(2, new Dictionary<string, string> { [competitor.ArticleNo] = "5억 1,000" }, 0, DateTime.UtcNow.AddMinutes(-10));
    var settings = new AppSettings { RankThreshold = 5 };
    var comparison = RankingAnalyzer.Compare(result, previous, settings);
    Assert(comparison.Events.Any(x => x.Title == "매물 랭킹 변경"), "랭킹 변경 알림");
    Assert(comparison.Events.Any(x => x.Title == "랭킹 기준 알림"), "기준 알림");
    Assert(comparison.Events.Any(x => x.Title == "동일매물 가격 변경"), "가격 알림");
    Assert(comparison.Events.Any(x => x.Title == "단독매물 상태 변경"), "신규 동일매물 알림");
    Assert(comparison.Events.All(x => x.ArticleNo == mine.ArticleNo), "모든 변동에 내 매물번호 연결");
    Assert(comparison.Events.All(x => x.ListingName == "테스트아파트 101동"), "모든 변동에 매물명 연결");
    Assert(comparison.Events.All(x => x.TradeSummary == "매매 5억"), "모든 변동에 거래정보 연결");
    Assert(comparison.Events.Single(x => x.Title == "매물 랭킹 변경").Highlight == NotificationHighlight.RankDown, "순위 하락 강조색 분류");
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

static void StreamListingsOneAtATime()
{
    var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"articleList":[
              {"articleNo":"2600000101","articleName":"첫 번째 매물"},
              {"articleNo":"2600000102","articleName":"두 번째 매물"}
            ],"isMoreData":false}
            """)
    });
    var configuration = new ApiConfiguration
    {
        RealtorArticleList = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            RealtorIdParameter = "realtorId",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string>()
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var enumerator = client.StreamOwnListingsAsync(
            new AppSettings { GroupId = "test-realtor" },
            CancellationToken.None)
        .GetAsyncEnumerator();
    try
    {
        Assert(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "첫 번째 행 반환");
        Assert(enumerator.Current.ArticleNo == "2600000101", "첫 번째 매물번호");
        Assert(enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "두 번째 행 반환");
        Assert(enumerator.Current.ArticleNo == "2600000102", "두 번째 매물번호");
        Assert(!enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult(), "스트림 종료");
    }
    finally
    {
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void StopRepeatedListingPages()
{
    var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("{\"articleList\":[{\"articleNo\":\"2600000199\"}]}")
    });
    var configuration = new ApiConfiguration
    {
        RealtorArticleList = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            RealtorIdParameter = "realtorId",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string>()
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var listings = client.GetOwnListingsAsync(
            new AppSettings { GroupId = "test-realtor" },
            CancellationToken.None)
        .GetAwaiter().GetResult();

    Assert(listings.Count == 1, "반복 페이지 매물 중복 제외");
    Assert(handler.CallCount == 2, "신규 매물이 없는 반복 페이지에서 조회 중단");
}

static void PersistListingCacheByLoginAndGroup()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NaverPropertyRanking-cache-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStore(directory);
        var firstListing = new Listing(
            "2600000201", "첫 번째 주소", "매매", "5억", "우리부동산", "group-a", "", "101동", "10/20", "84", true);
        var firstRanking = new RankingResult(firstListing, 2, 3, "5억", "5억 2,000", [firstListing]);
        store.SaveListingCache(new ListingCacheEntry(
            "testuser", "group-a", DateTime.UtcNow, [firstListing], [firstRanking]));

        var secondListing = firstListing with { ArticleNo = "2600000202", RealtorId = "group-b" };
        store.SaveListingCache(new ListingCacheEntry(
            "testuser", "group-b", DateTime.UtcNow, [secondListing], []));

        var cachePath = Path.Combine(directory, "listing-cache.inf");
        Assert(File.Exists(cachePath), "실행 위치의 inf 캐시 생성");
        var encryptedContents = File.ReadAllText(cachePath);
        Assert(!encryptedContents.Contains("testuser", StringComparison.OrdinalIgnoreCase), "로그인 ID 암호화");
        Assert(!encryptedContents.Contains("group-a", StringComparison.OrdinalIgnoreCase), "단체 ID 암호화");
        Assert(!encryptedContents.Contains(firstListing.ArticleNo, StringComparison.Ordinal), "매물 정보 암호화");

        var restored = store.LoadListingCache("TESTUSER", "GROUP-A");
        Assert(restored is not null, "로그인·단체 캐시 조회");
        Assert(restored!.Listings.Single().ArticleNo == firstListing.ArticleNo, "단체별 매물 분리");
        Assert(restored.RankingResults.Single().Rank == 2, "랭킹 표시 정보 복원");
        Assert(store.LoadListingCache("other-user", "group-a") is null, "로그인별 캐시 분리");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void SortListingsByRankingAndDuplicates()
{
    var first = new Listing("2600000301", "A", "", "", "", "", "", "", "", "", true);
    var second = new Listing("2600000302", "B", "", "", "", "", "", "", "", "", true);
    var third = new Listing("2600000303", "C", "", "", "", "", "", "", "", "", true);
    var pending = new Listing("2600000304", "D", "", "", "", "", "", "", "", "", true);
    var listings = new[] { first, second, third, pending };
    var rankings = new Dictionary<string, RankingResult>
    {
        [first.ArticleNo] = new RankingResult(first, 3, 7, null, null, []),
        [second.ArticleNo] = new RankingResult(second, 1, 12, null, null, []),
        [third.ArticleNo] = new RankingResult(third, 8, 2, null, null, [])
    };

    Assert(
        ListingSorter.Sort(listings, rankings, ListingSortOrder.RankAscending)
            .Select(listing => listing.ArticleNo)
            .SequenceEqual([second.ArticleNo, first.ArticleNo, third.ArticleNo, pending.ArticleNo]),
        "랭킹 낮은순");
    Assert(
        ListingSorter.Sort(listings, rankings, ListingSortOrder.RankDescending)
            .Select(listing => listing.ArticleNo)
            .SequenceEqual([third.ArticleNo, first.ArticleNo, second.ArticleNo, pending.ArticleNo]),
        "랭킹 높은순");
    Assert(
        ListingSorter.Sort(listings, rankings, ListingSortOrder.DuplicateCountDescending)
            .Select(listing => listing.ArticleNo)
            .SequenceEqual([second.ArticleNo, first.ArticleNo, third.ArticleNo, pending.ArticleNo]),
        "동일매물 많은순");
    Assert(
        ListingSorter.Sort(listings, rankings, ListingSortOrder.DuplicateCountAscending)
            .Select(listing => listing.ArticleNo)
            .SequenceEqual([third.ArticleNo, first.ArticleNo, second.ArticleNo, pending.ArticleNo]),
        "동일매물 적은순");

    var pendingArticleNumbers = new HashSet<string>([first.ArticleNo, third.ArticleNo], StringComparer.Ordinal);
    var next = ListingSorter.Sort(listings, rankings, ListingSortOrder.DuplicateCountDescending)
        .First(listing => pendingArticleNumbers.Contains(listing.ArticleNo));
    Assert(next.ArticleNo == first.ArticleNo, "현재 정렬 상단의 미조회 매물 선택");
}

static void FilterSingleListings()
{
    var single = new Listing("2600000341", "단일", "", "", "", "", "", "", "", "", true);
    var duplicate = new Listing("2600000342", "동일매물 있음", "", "", "", "", "", "", "", "", true);
    var pending = new Listing("2600000343", "미조회", "", "", "", "", "", "", "", "", true);
    var failed = new Listing("2600000344", "실패", "", "", "", "", "", "", "", "", true);
    var rankings = new Dictionary<string, RankingResult>
    {
        [single.ArticleNo] = new RankingResult(single, 1, 1, null, null, [single]),
        [duplicate.ArticleNo] = new RankingResult(duplicate, 1, 2, null, null, [duplicate]),
        [failed.ArticleNo] = new RankingResult(failed, null, 0, null, null, [], "조회 실패")
    };
    var listings = new[] { single, duplicate, pending, failed };

    var filtered = ListingVisibilityFilter.Apply(listings, rankings, true);
    Assert(!filtered.Any(x => x.ArticleNo == single.ArticleNo), "동일매물 수 1인 매물 숨김");
    Assert(filtered.Any(x => x.ArticleNo == duplicate.ArticleNo), "동일매물 수 2 이상 표시");
    Assert(filtered.Any(x => x.ArticleNo == pending.ArticleNo), "미조회 매물은 순위 조회를 위해 표시");
    Assert(filtered.Any(x => x.ArticleNo == failed.ArticleNo), "조회 실패 매물 표시");

    rankings[single.ArticleNo] = new RankingResult(single, 1, 2, null, null, [single, duplicate]);
    Assert(
        ListingVisibilityFilter.Apply(listings, rankings, true).Any(x => x.ArticleNo == single.ArticleNo),
        "순위 재조회 후 동일매물 증가 시 다시 표시");
    Assert(ListingVisibilityFilter.Apply(listings, rankings, false).Count == listings.Length, "검색조건 해제 시 전체 표시");
}

static void MergeOnlyMissingListings()
{
    var cached = new[]
    {
        new Listing("2600000351", "캐시 A", "", "", "", "group", "", "", "", "", true),
        new Listing("2600000352", "캐시 B", "", "", "", "group", "", "", "", "", true)
    };
    var latest = new[]
    {
        cached[1] with { Address = "최신 B" },
        new Listing("2600000353", "신규 C", "", "", "", "group", "", "", "", "", false),
        new Listing("2600000353", "중복 C", "", "", "", "group", "", "", "", "", false)
    };

    var merged = ListingCollectionMerger.AppendMissing(cached, latest);

    Assert(merged.Listings.Count == 3, "전체 매물 중복 제외");
    Assert(merged.AddedListings.Count == 1, "신규 매물만 반환");
    Assert(merged.Listings[1].Address == "최신 B", "기존 매물 표시 정보 갱신");
    Assert(merged.AddedListings[0].ArticleNo == "2600000353", "신규 매물 식별");
    Assert(merged.AddedListings[0].IsMine, "신규 매물을 내 매물로 표시");
}

static void FormatListingLookupProgress()
{
    Assert(
        ListingProgressFormatter.Format(3, 10) == "전체 10건 중 조회건수/리스트건수: 3/10",
        "조회건수와 리스트건수 표시");
    Assert(
        ListingProgressFormatter.Format(11, 10, "2600000401") ==
        "전체 10건 중 조회건수/리스트건수: 10/10 · 2600000401",
        "조회건수 범위와 매물번호 표시");
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
        new NotificationEvent("동일매물 가격 변경", "A부동산 가격 5억 → 4억 9,000", "2600000001", "테스트빌딩", "매매 5억"),
        new NotificationEvent("매물 랭킹 변경", "5위 → 3위", "2600000001", "테스트아파트 101동", "매매 5억"),
        new NotificationEvent("단독매물 상태 변경", "동일매물 1건 신규", "2600000002", "테스트창고", "월세 2,000/100")
    ]);
    Assert(text.Contains("테스트아파트 101동 | 매매 5억 | [매물 랭킹 변경] 5위 → 3위"), "매물명·거래정보·변동 형식");
    Assert(text.Contains("테스트빌딩 | 매매 5억 | [동일매물 가격 변경]"), "가격 변경 별도 메시지");
    Assert(text.Contains("테스트창고 | 월세 2,000/100 | [단독매물 상태 변경]"), "동일매물 별도 메시지");
    Assert(RankingNotificationFormatter.Format([]) == "변동 내역이 없습니다.", "변동 없음 문구");
}

static void PlanSeparateNotificationPopups()
{
    var events = new[]
    {
        new NotificationEvent("매물 랭킹 변경", "5위 → 3위"),
        new NotificationEvent("매물 랭킹 변경", "2위 → 4위"),
        new NotificationEvent("단독매물 상태 변경", "동일매물 1건 신규"),
        new NotificationEvent("동일매물 가격 변경", "5억 → 4억 9,000")
    };

    var popups = NotificationPopupPlanner.Create(events);
    Assert(popups.Count == 3, "변동상태별 팝업 분리");
    Assert(popups[0].WindowTitle == "순위변동 알림" && popups[0].Events.Count == 2, "순위변동 묶음 팝업");
    Assert(popups[1].WindowTitle == "동일매물 가격변동 알림", "가격변동 팝업");
    Assert(popups[2].WindowTitle == "동일매물 추가 알림", "동일매물 추가 팝업");

    var completion = NotificationPopupPlanner.Create([]);
    Assert(completion.Count == 1 && completion[0].WindowTitle == "순위조회 완료", "변동 없음 완료 팝업");

    var replacements = NotificationPopupPlanner.SelectTitlesToReplace(
        ["순위변동 알림", "동일매물 추가 알림", "동일매물 가격변동 알림"],
        NotificationPopupPlanner.Create(
        [
            new NotificationEvent("매물 랭킹 변경", "3위 → 2위"),
            new NotificationEvent("단독매물 상태 변경", "동일매물 2건 신규")
        ]));
    Assert(replacements.SetEquals(["순위변동 알림", "동일매물 추가 알림"]), "같은 유형 팝업만 최신 내용으로 교체");
    Assert(!replacements.Contains("동일매물 가격변동 알림"), "새 내용 없는 기존 팝업 유지");
}

static void ReconcileCurrentListings()
{
    var current = new[]
    {
        new Listing("2600000361", "유지 전", "", "", "", "group", "", "", "", "", true),
        new Listing("2600000362", "삭제", "", "", "", "group", "", "", "", "", true)
    };
    var latest = new[]
    {
        current[0] with { Address = "유지 최신" },
        new Listing("2600000363", "신규", "", "", "", "group", "", "", "", "", false)
    };

    var result = ListingCollectionMerger.Reconcile(current, latest);

    Assert(result.Listings.Select(x => x.ArticleNo).SequenceEqual(["2600000361", "2600000363"]), "API 최신 목록 반영");
    Assert(result.Listings[0].Address == "유지 최신", "유지 매물 정보 갱신");
    Assert(result.AddedListings.Single().ArticleNo == "2600000363", "신규 항목 추가");
    Assert(result.RemovedListings.Single().ArticleNo == "2600000362", "API 누락 항목 삭제");
    Assert(result.Listings.All(x => x.IsMine), "동기화 목록 내 매물 표시");
}

static void RunListingAndRankingConcurrently()
{
    using var handler = new ConcurrentEndpointHandler();
    var configuration = new ApiConfiguration
    {
        RealtorArticleList = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            RealtorIdParameter = "realtorId",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string>()
        },
        Ranking = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string>()
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var settings = new AppSettings { GroupId = "group-a" };
    var own = new Listing("2600000369", "테스트", "매매", "5억", "", "group-a", "", "", "", "", true);

    var listingTask = client.GetOwnListingsAsync(settings, CancellationToken.None);
    var rankingTask = client.GetRankingAsync(
        own,
        new HashSet<string> { own.ArticleNo },
        settings,
        CancellationToken.None);
    Task.WhenAll(listingTask, rankingTask).GetAwaiter().GetResult();

    Assert(handler.MaxConcurrency >= 2, "목록과 순위 HTTP 요청 동시 진행");
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
                Content = new StringContent("{\"success\":true,\"code\":\"HEARTBEAT_OK\",\"message\":\"접속 상태가 갱신되었습니다.\",\"notices\":[\"변경된 공지\"]}")
            };
        if (body.Contains("\"action\":\"logout\""))
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"code\":\"LOGOUT_SUCCESS\",\"message\":\"로그아웃되었습니다.\"}")
            };
        if (body.Contains("\"action\":\"saveMemberGroup\""))
        {
            Assert(body.Contains("\"groupId\":\"realtor-123\""), "단체 ID 전송");
            Assert(body.Contains("\"sessionId\":\"session-id\""), "단체 저장 세션 ID 전송");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"code\":\"MEMBER_GROUP_ADDED\",\"message\":\"조회한 단체 ID를 저장했습니다.\"}")
            };
        }
        Assert(body.Contains("\"action\":\"login\""), "로그인 action 전송");
        Assert(body.Contains("\"deviceId\":"), "PC 식별자 전송");
        Assert(body.Contains("203.0.113.10"), "공인 IP 전송");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"success":true,"code":"LOGIN_SUCCESS","message":"로그인되었습니다.","userId":"testuser","name":"홍길동","token":"device-token","sessionId":"session-id","membershipStart":"2026-08-01T00:00:00+09:00","membershipEnd":"2026-09-01T00:00:00+09:00","allowedPcCount":2,"currentPcCount":1,"notices":["첫 번째 공지","두 번째 공지"]}
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
    Assert(result.Session.Notices.SequenceEqual(new[] { "첫 번째 공지", "두 번째 공지" }), "공지사항 응답");
    var heartbeat = client.HeartbeatAsync(result.Session, CancellationToken.None).GetAwaiter().GetResult();
    Assert(heartbeat.Success && heartbeat.Code == "HEARTBEAT_OK", "heartbeat 갱신");
    Assert(heartbeat.Notices?.SequenceEqual(new[] { "변경된 공지" }) == true, "heartbeat 공지 갱신");
    var savedGroup = client.SaveMemberGroupAsync(result.Session, "realtor-123", CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(savedGroup.Success && savedGroup.Code == "MEMBER_GROUP_ADDED", "회원 단체 저장");
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
            [
              {"tag_name":"OtherApplication-v9.0.0","assets":[{"name":"NaverPropertyRanking.exe","browser_download_url":"https://github.com/example/repo/releases/download/other/NaverPropertyRanking.exe"}]},
              {"tag_name":"NaverPropertyRanking-v1.2.0","assets":[{"name":"NaverPropertyRanking.exe","browser_download_url":"https://github.com/example/repo/releases/download/NaverPropertyRanking-v1.2.0/NaverPropertyRanking.exe"}]},
              {"tag_name":"NaverPropertyRanking-v1.1.9","assets":[]}
            ]
            """)
    });
    using var service = new GitHubUpdateService(new UpdateConfiguration
    {
        Enabled = true,
        CheckOnStartup = true,
        CurrentVersion = "1.1.0",
        ReleasesApiUrl = "https://api.github.com/repos/example/repo/releases?per_page=100",
        ReleaseTagPrefix = "NaverPropertyRanking-v",
        AssetName = "NaverPropertyRanking.exe"
    }, handler);
    var result = service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.UpdateAvailable, "새 버전 판정");
    Assert(result.LatestVersion == "1.2.0", "최신 태그 파싱");
    Assert(result.DownloadUrl.EndsWith("NaverPropertyRanking.exe"), "릴리스 EXE 자산 URL");
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

sealed class ConcurrentEndpointHandler : HttpMessageHandler
{
    private int _activeRequests;
    private int _maxConcurrency;

    public int MaxConcurrency => _maxConcurrency;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _activeRequests);
        UpdateMaximum(active);
        try
        {
            await Task.Delay(100, cancellationToken);
            var isRanking = request.RequestUri?.Query.Contains("representativeArticleNo") == true;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(isRanking
                    ? "[{\"articleNo\":\"2600000369\"}]"
                    : "{\"articleList\":[{\"articleNo\":\"2600000369\"}],\"isMoreData\":false}")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var current = _maxConcurrency;
            if (value <= current || Interlocked.CompareExchange(ref _maxConcurrency, value, current) == current)
                return;
        }
    }
}
