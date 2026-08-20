using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

var tests = new List<(string Name, Action Run)>
{
    ("배열 응답 파싱", ParseArrayResponse),
    ("래핑된 응답 파싱", ParseWrappedResponse),
    ("상가·창고 소재지·매물유형·설명 분리", ParseCommercialListingDetails),
    ("쿠키 사전 변환", NormalizeCookies),
    ("알림 변화 감지", DetectChanges),
    ("매물번호 직접 조회 우선", DirectArticleNumbersTakePriority),
    ("매물 목록 행 단위 스트림", StreamListingsOneAtATime),
    ("반복 매물 페이지 조회 중단", StopRepeatedListingPages),
    ("로그인·단체별 매물 로컬 캐시", PersistListingCacheByLoginAndGroup),
    ("리스트 컬럼 순서 설정 저장", PersistGridColumnOrder),
    ("목록·상세 Excel 출력", ExportExcelWorkbook),
    ("상세 Excel 동일매물 2건 이상 필터", FilterExcelDetailResults),
    ("매물 랭킹·동일매물 정렬", SortListingsByRankingAndDuplicates),
    ("단일매물 제외 및 재표시", FilterSingleListings),
    ("캐시와 최신 매물 신규 항목 병합", MergeOnlyMissingListings),
    ("현재 목록과 API 결과 추가·삭제 동기화", ReconcileCurrentListings),
    ("매물목록과 순위 API 동시 실행", RunListingAndRankingConcurrently),
    ("랭킹 API 제한 병렬 실행", RunRankingRequestsConcurrently),
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
    ("광고분석 단지 그룹·중개사 상위 3곳", AnalyzeComplexAdvertisements),
    ("단지 정보 응답 파싱", ParseComplexInformationResponse),
    ("단지 광고 중개인 응답 파싱", ParseAdvertisementRealtorNamesResponse),
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
          {"articleNo":"2600000001","complexNo":"109250","articleName":"테스트아파트","buildingName":"101동","tradeTypeName":"매매","dealOrWarrantPrc":"5억","sameAddrCnt":2,"sameAddrMinPrc":"4억 9,000","sameAddrMaxPrc":"5억 1,000","realtorName":"우리부동산","verificationTypeCode":"DOC","articleConfirmYmd":"20260818"},
          {"articleNo":"2600000002","articleName":"테스트아파트","buildingName":"101동","tradeTypeName":"매매","dealOrWarrantPrc":"5억 1,000","realtorName":"다른부동산","complexInfo":{"complexNumber":111222}},
          {"articleNo":"2600000005","articleName":"URL단지","tradeTypeName":"매매","dealOrWarrantPrc":"3억","articleUrl":"https://new.land.naver.com/complexes/333444?articleNo=2600000005"}
        ]
        """;
    var parsed = NaverResponseParser.ParseArticleResponse(json, new HashSet<string> { "2600000001" });
    Assert(parsed.Listings.Count == 3, "목록 수");
    Assert(parsed.Listings[0].IsMine, "내 매물 식별");
    Assert(parsed.Listings[0].Address == "테스트아파트 · 101동", "주소 조합");
    Assert(parsed.Listings[0].ComplexNo == "109250", "단지번호 파싱");
    Assert(parsed.Listings[1].ComplexNo == "111222", "중첩 complexNumber 파싱");
    Assert(parsed.Listings[2].ComplexNo == "333444", "complexes URL 단지번호 파싱");
    Assert(parsed.Listings[0].RegisteredDate == "20260818", "등록일 파싱");
    Assert(parsed.Listings[0].SameAddressCount == 2, "sameAddrCnt 파싱");
    Assert(parsed.Listings[0].VerificationTypeCode == "DOC", "검증방식 코드 파싱");
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
    Assert(!listing.Address.Contains("대로변 상가 1층"), "설명과 매물명 분리");
    Assert(listing.Address.Contains("1/5층"), "층 정보 표시");
    Assert(listing.Location == "서울시 강남구 역삼동", "소재지 별도 파싱");
    Assert(listing.RealEstateType == "상가", "매물유형 파싱");
    Assert(listing.Description == "대로변 상가 1층", "설명 별도 파싱");
    Assert(listing.Price == "5,000/300", "상가 월세 표시");
}

static void NormalizeCookies()
{
    var cookie = NaverLandClient.NormalizeCookieHeader("cookies = {'NAC': 'abc', 'NNB': 'def'}");
    Assert(cookie == "NAC=abc; NNB=def", "Python 사전 변환");
}

static void DetectChanges()
{
    Assert(!new AppSettings().NotifyRankThreshold, "랭킹 기준 알림 기본 해제");
    Assert(new AppSettings().PollIntervalMinutes == 30, "조회 간격 기본 30분");
    Assert(!new AppSettings().PopupNotificationsEnabled, "팝업 알림 기본 해제");
    Assert(AppSettings.NormalizePollInterval(2) == 10, "조회 간격 최소 10분");
    Assert(!new AppSettings().PropertyAnalysisEnabled, "물건분석 기본 해제");
    var mine = new Listing("2600000001", "서울시 강남구 · 테스트아파트 · 101동 · 남향 올수리 · 10/20층", "매매", "5억", "우리부동산", "mine", "", "101동", "10/20", "84", true)
    {
        ArticleName = "테스트아파트",
        Description = "남향 올수리"
    };
    var competitor = new Listing("2600000002", "테스트아파트 101동", "매매", "5억 2,000", "다른부동산", "other", "", "101동", "10/20", "84");
    var result = new RankingResult(mine, 5, 2, "5억", "5억 2,000", [mine, competitor]);
    var previous = new ListingSnapshot(2, new Dictionary<string, string> { [competitor.ArticleNo] = "5억 1,000" }, 0, DateTime.UtcNow.AddMinutes(-10));
    var settings = new AppSettings { RankThreshold = 5, NotifyRankThreshold = true };
    var comparison = RankingAnalyzer.Compare(result, previous, settings);
    Assert(comparison.Events.Any(x => x.Title == "매물 랭킹 변경"), "랭킹 변경 알림");
    Assert(comparison.Events.Any(x => x.Title == "랭킹 기준 알림"), "기준 알림");
    Assert(comparison.Events.Any(x => x.Title == "동일매물 가격 변경"), "가격 알림");
    Assert(comparison.Events.Any(x => x.Title == "단독매물 상태 변경"), "신규 동일매물 알림");
    Assert(comparison.Events.All(x => x.ArticleNo == mine.ArticleNo), "모든 변동에 내 매물번호 연결");
    Assert(comparison.Events.All(x => x.ListingName == mine.Address), "모든 변동에 목록과 동일한 매물종류/설명 연결");
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
        store.RemoveListingCache("testuser", "group-a");
        Assert(store.LoadListingCache("testuser", "group-a") is null, "기존 단체 매물 캐시 전체 삭제");
        Assert(store.LoadListingCache("testuser", "group-b") is not null, "다른 단체 캐시 유지");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void PersistGridColumnOrder()
{
    var directory = Path.Combine(Path.GetTempPath(), $"NaverPropertyRanking-column-order-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStore(directory, directory);
        var expected = new List<string> { "Selected", "ArticleNo", "PropertyType", "Price", "Description" };
        store.SaveSettings(new AppSettings { GridColumnOrder = expected, PollIntervalMinutes = 2 });

        var restored = store.LoadSettings();
        Assert(restored.GridColumnOrder.SequenceEqual(expected), "컬럼 순서 저장 및 복원");
        Assert(restored.PollIntervalMinutes == 10, "조회 간격 최소값 저장 보정");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void ExportExcelWorkbook()
{
    var requestedOutput = Environment.GetEnvironmentVariable("NPR_EXCEL_TEST_OUTPUT");
    var directory = string.IsNullOrWhiteSpace(requestedOutput)
        ? Path.Combine(Path.GetTempPath(), $"NaverPropertyRanking-excel-{Guid.NewGuid():N}")
        : Path.GetDirectoryName(Path.GetFullPath(requestedOutput))!;
    var outputPath = string.IsNullOrWhiteSpace(requestedOutput)
        ? Path.Combine(directory, "매물목록.xlsx")
        : Path.GetFullPath(requestedOutput);
    try
    {
        var columns = new List<ExcelExportColumn>
        {
            new("Mine", "구분"),
            new("ArticleNo", "매물번호"),
            new("PropertyType", "매물유형"),
            new("Price", "거래금액"),
            new("CurrentRank", "현재랭킹"),
            new("Description", "설명")
        };
        var ownValues = new Dictionary<string, string>
        {
            ["Mine"] = "내 매물",
            ["ArticleNo"] = "2600000001",
            ["PropertyType"] = "아파트",
            ["Price"] = "5억",
            ["CurrentRank"] = "33위 ↑2",
            ["Description"] = "남향 올수리"
        };
        var detailColumns = new List<ExcelExportColumn>
        {
            new("CurrentRank", "현재랭킹"),
            new("Movement", "변동"),
            new("PreviousRank", "이전랭킹"),
            new("Total", "동일매물"),
            new("ArticleNo", "매물번호"),
            new("BuildingName", "단지/건물명"),
            new("Location", "주소"),
            new("PropertyType", "매물종류"),
            new("Trade", "거래유형"),
            new("Price", "금액"),
            new("RegisteredDate", "등록일"),
            new("Provider", "CP사"),
            new("Realtor", "중개사무소"),
            new("GroupId", "단체ID")
        };
        var detailOwnValues = new Dictionary<string, string>
        {
            ["CurrentRank"] = "33",
            ["Movement"] = "▲2",
            ["PreviousRank"] = "35",
            ["Total"] = "47",
            ["ArticleNo"] = "2600000001",
            ["BuildingName"] = "테스트아파트 · 101동",
            ["Location"] = "서울시 강남구",
            ["PropertyType"] = "아파트",
            ["Trade"] = "매매",
            ["Price"] = "5억",
            ["RegisteredDate"] = "26.08.18",
            ["Provider"] = "네이버부동산",
            ["Realtor"] = "우리부동산",
            ["GroupId"] = "test-group"
        };
        var detailComparableValues = new Dictionary<string, string>(detailOwnValues)
        {
            ["CurrentRank"] = "1",
            ["Movement"] = "내 매물",
            ["PreviousRank"] = string.Empty,
            ["Total"] = string.Empty,
            ["ArticleNo"] = "└ 2600000002",
            ["GroupId"] = "test-group"
        };
        var nextDetailOwnValues = new Dictionary<string, string>(detailOwnValues)
        {
            ["CurrentRank"] = "20",
            ["Movement"] = string.Empty,
            ["PreviousRank"] = "20",
            ["ArticleNo"] = "2600000003"
        };

        ExcelExportService.Export(
            outputPath,
            columns,
            [new ExcelExportRow(ownValues, HighlightedColumns: new HashSet<string> { "CurrentRank" })],
            detailColumns,
            [
                new ExcelExportRow(detailOwnValues, HighlightGroupHeader: true),
                new ExcelExportRow(detailComparableValues, HighlightMine: true),
                new ExcelExportRow(new Dictionary<string, string>(), IsSeparator: true),
                new ExcelExportRow(nextDetailOwnValues, HighlightGroupHeader: true)
            ]);

        Assert(File.Exists(outputPath), "xlsx 파일 생성");
        using var archive = System.IO.Compression.ZipFile.OpenRead(outputPath);
        Assert(archive.GetEntry("xl/workbook.xml") is not null, "워크북 XML 생성");
        Assert(archive.GetEntry("xl/worksheets/sheet1.xml") is not null, "목록 시트 생성");
        var workbookXml = ReadZipEntry(archive, "xl/workbook.xml");
        var listXml = ReadZipEntry(archive, "xl/worksheets/sheet1.xml");
        var detailXml = ReadZipEntry(archive, "xl/worksheets/sheet2.xml");
        Assert(workbookXml.Contains("목록", StringComparison.Ordinal) && workbookXml.Contains("상세", StringComparison.Ordinal), "목록·상세 시트명");
        Assert(detailXml.Contains("주소", StringComparison.Ordinal) && detailXml.Contains("CP사", StringComparison.Ordinal), "상세 전용 필드 구성");
        Assert(detailXml.Contains("26.08.18", StringComparison.Ordinal), "상세 등록일 표시");
        Assert(detailXml.Contains("내 매물", StringComparison.Ordinal), "순위 목록 내 매물 표시");
        Assert(detailXml.Contains("└ 2600000002", StringComparison.Ordinal), "하위 매물번호 표시");
        Assert(!detailXml.Contains("outlineLevel", StringComparison.Ordinal) && !detailXml.Contains("outlinePr", StringComparison.Ordinal), "상세 트리 버튼 제거");
        Assert(listXml.Contains("r=\"E2\" s=\"4\"", StringComparison.Ordinal), "목록 현재랭킹 배경색");
        Assert(detailXml.Contains("r=\"A2\" s=\"5\"", StringComparison.Ordinal), "상세 최상위 매물 연한 회색 배경");
        Assert(detailXml.Contains("r=\"A3\" s=\"2\"", StringComparison.Ordinal), "상세 순위 내 매물 배경색");
        Assert(detailXml.Contains("<row r=\"4\" ht=\"22\"", StringComparison.Ordinal) &&
               detailXml.Contains("r=\"A4\" s=\"6\"", StringComparison.Ordinal), "매물 그룹 사이 빈 행");
        Assert(detailXml.Contains("r=\"A5\" s=\"5\"", StringComparison.Ordinal), "빈 행 다음 매물 정보");
    }
    finally
    {
        if (string.IsNullOrWhiteSpace(requestedOutput) && Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}

static void FilterExcelDetailResults()
{
    var single = new Listing("2600000101", "단일매물", "매매", "5억", "", "", "", "", "", "", true);
    var duplicate = new Listing("2600000102", "중복매물", "매매", "6억", "", "", "", "", "", "", true);
    var competitor = new Listing("2600000103", "중복매물", "매매", "6억", "", "", "", "", "", "");
    var results = new[]
    {
        new RankingResult(single, 1, 1, null, null, [single]),
        new RankingResult(duplicate, 1, 2, null, null, [duplicate, competitor])
    };

    var selected = ExcelDetailResultSelector.Select(results);
    Assert(selected.Count == 1 && selected[0].OwnListing.ArticleNo == duplicate.ArticleNo,
        "동일매물 2건 이상만 상세 시트 대상으로 선택");

    // 내 매물 3건이 서로 동일매물이면 목록 순서상 첫 매물만 트리를 만든다.
    var mine1 = new Listing("2600000201", "동일매물", "매매", "7억", "", "", "", "", "", "", true);
    var mine2 = new Listing("2600000202", "동일매물", "매매", "7억", "", "", "", "", "", "", true);
    var mine3 = new Listing("2600000203", "동일매물", "매매", "7억", "", "", "", "", "", "", true);
    var otherOwn = new Listing("2600000301", "다른매물", "매매", "9억", "", "", "", "", "", "", true);
    var otherRival = new Listing("2600000302", "다른매물", "매매", "9억", "", "", "", "", "", "");
    var duplicateResults = new[]
    {
        new RankingResult(mine1, 1, 3, null, null, [mine1, mine2, mine3]),
        new RankingResult(mine2, 2, 3, null, null, [mine1, mine2, mine3]),
        new RankingResult(otherOwn, 1, 2, null, null, [otherOwn, otherRival]),
        new RankingResult(mine3, 3, 3, null, null, [mine1, mine2, mine3])
    };

    var deduplicated = ExcelDetailResultSelector.Select(duplicateResults);
    Assert(deduplicated.Count == 2, "동일 매물은 트리 1개만 생성");
    Assert(deduplicated[0].OwnListing.ArticleNo == mine1.ArticleNo, "동일 매물 중 첫 매물이 트리 상위");
    Assert(deduplicated[1].OwnListing.ArticleNo == otherOwn.ArticleNo, "다른 매물은 트리 유지");
    Assert(
        deduplicated.All(item => item.OwnListing.ArticleNo != mine2.ArticleNo &&
                                 item.OwnListing.ArticleNo != mine3.ArticleNo),
        "이미 하위에 표시된 매물은 새 트리 미생성");
}

static string ReadZipEntry(System.IO.Compression.ZipArchive archive, string name)
{
    using var stream = archive.GetEntry(name)?.Open()
                       ?? throw new InvalidOperationException($"ZIP 항목 없음: {name}");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
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

static void RunRankingRequestsConcurrently()
{
    using var handler = new ConcurrentEndpointHandler(250);
    var configuration = new ApiConfiguration
    {
        Ranking = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles",
            Headers = new Dictionary<string, string>(),
            Params = new Dictionary<string, string>()
        }
    };
    using var client = new NaverLandClient(configuration, handler);
    var settings = new AppSettings { GroupId = "group-a" };
    var listings = Enumerable.Range(1, 5)
        .Select(index => new Listing($"26000004{index:00}", "테스트", "매매", "5억", "", "group-a", "", "", "", "", true))
        .ToList();
    var ownNumbers = listings.Select(listing => listing.ArticleNo).ToHashSet(StringComparer.Ordinal);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    Task.WhenAll(listings.Select(listing =>
            client.GetRankingAsync(listing, ownNumbers, settings, CancellationToken.None)))
        .GetAwaiter()
        .GetResult();
    stopwatch.Stop();

    Assert(handler.MaxConcurrency >= 2, "랭킹 HTTP 요청 병렬 진행");
    Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "기존 3초 고정 대기 제거");
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
                {"success":true,"code":"LOGIN_SUCCESS","message":"로그인되었습니다.","userId":"testuser","name":"홍길동","token":"device-token","sessionId":"session-id","membershipStart":"2026-08-01T00:00:00+09:00","membershipEnd":"2026-09-01T00:00:00+09:00","allowedPcCount":2,"currentPcCount":1,"grade":2,"notices":["첫 번째 공지","두 번째 공지"]}
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
    Assert(result.Session.Grade == 2, "회원 등급 응답");
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

static void AnalyzeComplexAdvertisements()
{
    var ownedListings = new[]
    {
        new Listing("1", "주소1", "매매", "5억", "내부동산", "mine", "네이버", "101동", "", "", true)
        {
            ComplexNo = "109250", ArticleName = "테스트아파트"
        },
        new Listing("2", "주소2", "전세", "3억", "내부동산", "mine", "네이버", "102동", "", "", true)
        {
            ComplexNo = "109250", ArticleName = "테스트아파트"
        },
        new Listing("3", "주소3", "매매", "4억", "다른단지", "mine2", "네이버", "", "", "", true)
        {
            ComplexNo = "200000", ArticleName = "다른아파트"
        },
        new Listing("4", "단독주소", "매매", "2억", "단독", "mine3", "네이버", "", "", "", true)
    };
    var complexes = AdvertisementAnalysisService.GroupOwnedComplexes(ownedListings);
    Assert(complexes.Count == 2, "complexNo 보유 단지만 그룹화");
    Assert(complexes.Single(item => item.ComplexNo == "109250").OwnedListingCount == 2, "단지 보유 매물 수");
    Assert(complexes.Single(item => item.ComplexNo == "109250").ComplexName == "테스트아파트", "단지명 표시");
    Assert(complexes.Single(item => item.ComplexNo == "109250").ArticleNumbers?.SequenceEqual(new[] { "1", "2" }) == true,
        "체크 매물번호 표시");

    const string detailJson = """
        {
          "articleDetail":{
            "articleNo":"2641185157","articleName":"DMC한강자이더헤리티지","complexNo":"148338",
            "realEstateTypeName":"아파트","tradeTypeName":"매매","floorInfo":"13/24",
            "correspondingFloorCount":"13","totalFloorCount":"24","area1":"112.3","area2":"84.9",
            "areaName":"84A","directionTypeName":"남향","roomCount":"3","bathroomCount":"2",
            "entranceTypeName":"계단식","dealPrice":"15억 5,000","monthlyManagementCost":"25",
            "moveInTypeName":"협의","moveInPossibleYmd":"20260901","articleConfirmYMD":"20260819",
            "verificationTypeCode":"OWNER","verificationTypeName":"소유자확인","articleStatusCode":"R0"
          },
          "articleFacility":{
            "totalParkingCount":"1200","parkingCountPerHousehold":"1.2",
            "heatingAndCoolingSystemTypeName":"지역난방","heatingEnergyTypeName":"열병합",
            "articleOptions":[{"name":"에어컨"},{"name":"붙박이장"}],
            "securityFacilityList":["CCTV","인터폰"]
          },
          "articlePhotos":[{"imageUrl":"1"},{"imageUrl":"2"}],
          "articleRealtor":{"realtorName":"덕은역조대표부동산","cpId":"naver","cpName":"네이버"}
        }
        """;
    var identity = NaverResponseParser.ParseArticleComplexIdentity(detailJson);
    Assert(identity.ComplexNo == "148338", "상세 articleDetail 단지번호 파싱");
    Assert(identity.ComplexName == "DMC한강자이더헤리티지", "상세 articleDetail 단지명 파싱");
    var detail = NaverResponseParser.ParseArticleComparisonDetail(
        detailJson,
        new Listing("2641185157", "", "매매", "15억 5,000", "", "", "", "", "", "", true));
    Assert(detail.Listing.ComplexNo == "148338" && detail.CorrespondingFloorCount == "13", "광고분석 단지·층 상세 파싱");
    Assert(detail.SupplyArea == "112.3" && detail.ExclusiveArea == "84.9" && detail.RoomCount == "3", "광고분석 면적·구조 파싱");
    Assert(detail.Options.Contains("에어컨") && detail.SecurityFacilities.Contains("CCTV"), "광고분석 시설 목록 파싱");
    Assert(detail.PhotoCount == 2 && detail.ProviderId == "naver", "광고분석 사진·제공사 파싱");
    Assert(detail.Listing.VerificationTypeCode == "OWNER", "상세 검증방식 코드 파싱");
    Assert(VerificationTypeFormatter.Format("DOC") == "구홍보", "DOC 검증방식 변환");
    Assert(VerificationTypeFormatter.Format("NDOC1") == "신홍보" && VerificationTypeFormatter.Format("NDOC2") == "신홍보", "NDOC 검증방식 변환");
    Assert(VerificationTypeFormatter.Format("MOBL") == "모바일V1", "MOBL 검증방식 변환");
    Assert(VerificationTypeFormatter.Format("OWNER") == "모바일V2", "OWNER 검증방식 변환");
    Assert(VerificationTypeFormatter.Format("ETC") == "현장확인", "기타 검증방식 변환");

    var competitorDetail = detail with
    {
        Listing = detail.Listing with
        {
            ArticleNo = "2641185000",
            Price = "15억",
            RealtorName = "경쟁부동산",
            IsMine = false
        },
        DisplayPrice = "15억",
        DealPrice = "15억",
        Direction = "동향",
        PhotoCount = 3
    };
    var rankingResult = new RankingResult(detail.Listing, 4, 4, null, null,
    [
        competitorDetail.Listing,
        detail.Listing,
        competitorDetail.Listing with
        {
            ArticleNo = "2641185001",
            RealtorName = "둘째부동산",
            RealtorId = "competitor-2"
        },
        competitorDetail.Listing with
        {
            ArticleNo = "2641185002",
            RealtorName = "셋째부동산",
            RealtorId = "competitor-3"
        }
    ]);
    var analysis = new AdvertisementListingAnalysis(
        rankingResult,
        detail,
        [new RankedArticleComparison(1, 1, competitorDetail)]);
    var fieldComparisons = AdvertisementAnalysisService.BuildFieldComparisons([analysis]);
    Assert(fieldComparisons.Count >= 40, "광고분석 요청 비교필드 구성");
    Assert(fieldComparisons.Single(row => row.FieldName == "방향").Result == "불일치", "광고분석 동일·불일치 표시");
    Assert(fieldComparisons.Single(row => row.FieldName == "매매가").Result.Contains("저렴"), "광고분석 가격 차액·차이율 표시");
    var topCompetitors = AdvertisementAnalysisService.SelectTopCompetitors(rankingResult);
    Assert(topCompetitors.Count == 3, "내 매물 제외 후 동일매물 최대 3개");
    Assert(topCompetitors.Select(item => item.Rank).SequenceEqual([1, 2, 3]), "경쟁 동일매물 비교순위 1~3 부여");
    Assert(topCompetitors.Select(item => item.ExposureRank).SequenceEqual([1, 3, 4]), "내 매물 제외 후 원본 노출순위 정렬");
    Assert(topCompetitors.All(item => item.Listing.ArticleNo != detail.Listing.ArticleNo), "광고분석에서 내 매물 제외");

    var duplicatedRealtorResult = rankingResult with
    {
        Comparables =
        [
            competitorDetail.Listing,
            competitorDetail.Listing with { ArticleNo = "2641185009" },
            competitorDetail.Listing with
            {
                ArticleNo = "2641185001",
                RealtorName = "둘째부동산",
                RealtorId = "competitor-2"
            }
        ]
    };
    var uniqueRealtors = AdvertisementAnalysisService.SelectTopCompetitors(duplicatedRealtorResult);
    Assert(uniqueRealtors.Count == 2, "광고분석 중개사 중복 제거");
    Assert(uniqueRealtors.Select(item => item.ExposureRank).SequenceEqual([1, 3]), "중개사별 최상위 노출매물 선택");

    var advertisements = new[]
    {
        new Listing("a1", "", "매매", "5억", "첫째부동산", "r1", "한경", "", "", ""),
        new Listing("a2", "", "매매", "5억 1,000", "첫째부동산", "r1", "한경", "", "", ""),
        new Listing("a3", "", "전세", "3억", "둘째부동산", "r2", "부동산114", "", "", ""),
        new Listing("a4", "", "매매", "5억 2,000", "셋째부동산", "r3", "선방", "", "", ""),
        new Listing("a5", "", "매매", "5억 3,000", "넷째부동산", "r4", "선방", "", "", "")
    };
    var top = AdvertisementAnalysisService.SelectTopRealtors(advertisements);
    Assert(top.Count == 3, "중개사 최대 3곳");
    Assert(top.Select(item => item.RealtorId).SequenceEqual(new[] { "r1", "r2", "r3" }), "광고 순서와 중복 제거");
    Assert(top.Select(item => item.Rank).SequenceEqual(new[] { 1, 2, 3 }), "광고 순위 부여");
}

static void ParseComplexInformationResponse()
{
    const string json = """
        {
          "complexDetail": {
            "complexNo":"15832","complexName":"경희궁자이(2단지)",
            "totalHouseholdCount":589,"totalDongCount":8,
            "lowFloor":8,"highFloor":20,
            "useApproveYmd":"20170224",
            "parkingPossibleCount":760,"parkingCountByHousehold":"1.29",
            "batlRatio":241,"btlRatio":33,
            "constructionCompanyName":"지에스건설(주)",
            "heatMethodTypeCode":"individual","heatFuelTypeCode":"gas",
            "managementOfficeTelNo":"02-737-2101",
            "address":"서울시 종로구 평동","detailAddress":"233",
            "roadAddressPrefix":"서울시 종로구","roadAddress":"경교장길 35",
            "pyoengNames":["81A","81D","81E","81F","102A1","103A","110A","111B","111C","148A"]
          }
        }
        """;
    var info = NaverResponseParser.ParseComplexInformation(json, "15832", "대체명");
    Assert(info.ComplexNo == "15832", "단지번호");
    Assert(info.ComplexName == "경희궁자이(2단지)", "단지명");
    Assert(info.HouseholdSummary == "589세대 (총8개동)", "세대수 조합");
    Assert(info.FloorRange == "8층/20층", "저/최고층");
    Assert(info.UseApproveDate == "2017년 02월 24일", "사용승인일 형식");
    Assert(info.ParkingSummary == "760대(세대당 1.29대)", "주차 조합");
    Assert(info.FloorAreaRatio == "241%", "용적률");
    Assert(info.BuildingCoverageRatio == "33%", "건폐율");
    Assert(info.ConstructionCompany == "지에스건설(주)", "건설사");
    Assert(info.Heating == "개별난방, 도시가스", "난방 코드 변환");
    Assert(info.ManagementOfficeTel == "02-737-2101", "관리사무소");
    Assert(info.Address == "서울시 종로구 평동 233", "주소 조합");
    Assert(info.RoadAddress == "서울시 종로구 경교장길 35", "도로명 조합");
    Assert(info.AreaNames.StartsWith("81A", StringComparison.Ordinal), "면적 목록");

    var fallback = NaverResponseParser.ParseComplexInformation("{}", "15832", "대체명");
    Assert(fallback.ComplexName == "대체명", "단지명 대체값");
    Assert(fallback.HouseholdSummary == string.Empty, "정보 없는 응답 빈 값 유지");
}

static void ParseAdvertisementRealtorNamesResponse()
{
    const string json = """
        {
          "isSuccess": true,
          "result": {
            "list": [
              {"articleNumber":"1","representativeInfo":{"realtorName":"첫째부동산","realtorId":"duckeun72"}},
              {"articleNumber":"2","realtorName":"둘째부동산","realtorId":"other1"},
              {"articleNumber":"3","brokerInfo":{"brokerName":"셋째부동산"}},
              {"articleNumber":"4","realtorName":"넷째부동산","realtorId":"other3"}
            ]
          }
        }
        """;
    var realtors = NaverResponseParser.ParseAdvertisementRealtors(json);
    Assert(realtors.Count == 3, "광고 중개인 최대 3명");
    Assert(realtors[0].RealtorName == "첫째부동산", "광고 1순위 중첩 필드");
    Assert(realtors[0].RealtorId == "duckeun72", "광고 1순위 중개인 ID");
    Assert(realtors[1].RealtorName == "둘째부동산", "광고 2순위 최상위 필드");
    Assert(realtors[1].RealtorId == "other1", "광고 2순위 중개인 ID");
    Assert(realtors[2].RealtorName == "셋째부동산", "광고 3순위 brokerName 대체");
    Assert(realtors[2].RealtorId == string.Empty, "광고 3순위 ID 없음 빈 값");

    var empty = NaverResponseParser.ParseAdvertisementRealtors("{\"result\":{\"list\":[]}}");
    Assert(empty.Count == 0, "광고 없음 빈 목록");
}

static void ApplyApiConfiguration()
{
    var handler = new StubHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(request.RequestUri?.AbsolutePath.Contains("/complex/") == true
            ? "{\"articleList\":[{\"articleNo\":\"a1\",\"realtorId\":\"r1\",\"realtorName\":\"첫째\"},{\"articleNo\":\"a2\",\"realtorId\":\"r1\",\"realtorName\":\"첫째\"},{\"articleNo\":\"a3\",\"realtorId\":\"r2\",\"realtorName\":\"둘째\"},{\"articleNo\":\"a4\",\"realtorId\":\"r3\",\"realtorName\":\"셋째\"},{\"articleNo\":\"a5\",\"realtorId\":\"r4\",\"realtorName\":\"넷째\"}],\"isMoreData\":false}"
            : request.RequestUri?.AbsolutePath == "/api/articles/2641185157"
                ? "{\"articleDetail\":{\"articleNo\":\"2641185157\",\"articleName\":\"DMC한강자이더헤리티지\",\"complexNo\":\"148338\"}}"
            : request.RequestUri?.AbsolutePath == "/api/complexes/15832"
                ? "{\"complexDetail\":{\"complexNo\":\"15832\",\"complexName\":\"경희궁자이\",\"totalHouseholdCount\":589}}"
            : request.RequestUri?.AbsolutePath == "/front-api/v1/realtor/advertisement"
                ? "{\"result\":{\"list\":[{\"realtorName\":\"광고1중개\",\"realtorId\":\"bizmk\"},{\"realtorName\":\"광고2중개\"},{\"realtorName\":\"광고3중개\"}]}}"
            : request.RequestUri?.Query.Contains("representativeArticleNo") == true
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
        },
        ArticleDetail = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles/{articleNo}",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer detail-token",
                ["Cookie"] = "DETAIL=test"
            }
        },
        ComplexDetail = new ApiEndpointConfiguration
        {
            Endpoint = "/api/complexes/{complexNo}",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer complex-token",
                ["Cookie"] = "CPLX=test"
            },
            Params = new Dictionary<string, string>()
        },
        RealtorAdvertisement = new ApiEndpointConfiguration
        {
            Endpoint = "https://fin.land.naver.com/front-api/v1/realtor/advertisement",
            Headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://fin.land.naver.com/",
                ["Cookie"] = "FIN=test"
            },
            Params = new Dictionary<string, string>
            {
                ["advertisementRerankChannelType"] = "property.complex.price",
                ["tradeTypes[]"] = "A1"
            }
        },
        ComplexAdvertising = new ApiEndpointConfiguration
        {
            Endpoint = "/api/articles/complex/{complexNo}",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer advertising-token",
                ["Cookie"] = "AD=test"
            },
            Params = new Dictionary<string, string>
            {
                ["order"] = "rank",
                ["page"] = "1"
            }
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

    var own = new Listing("2612345678", "직접", "", "", "", "", "", "", "", "", true)
    {
        ComplexNo = "109250"
    };
    var ranking = client.GetRankingAsync(own, new HashSet<string> { own.ArticleNo }, settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "ranking-token", "랭킹 Authorization 분리");
    Assert(handler.CookieHeader == "RANK=test", "랭킹 Cookie 분리");
    Assert(ranking.OwnListing.ComplexNo == "109250", "랭킹 응답 누락 시 기존 단지번호 유지");

    var missingComplex = new Listing("2641185157", "DMC한강자이더헤리티지", "매매", "11억", "", "", "", "105동", "", "", true);
    var hydrated = client.HydrateComplexIdentityAsync(missingComplex, settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(hydrated.ComplexNo == "148338", "상세 API 단지번호 보강");
    Assert(handler.LastRequestUri?.AbsolutePath == "/api/articles/2641185157", "매물 상세 경로 적용");
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "detail-token", "상세 Authorization 적용");
    Assert(handler.CookieHeader == "DETAIL=test", "상세 Cookie 적용");

    configuration.ArticleDetail.Headers.Clear();
    client.GetArticleComparisonDetailAsync(missingComplex, settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "list-token", "상세 헤더 미설정 시 목록 헤더 사용");
    Assert(handler.CookieHeader == "LIST=test", "상세 헤더 미설정 시 목록 Cookie 사용");

    var advertising = client.GetComplexAdvertisingRealtorsAsync("109250", settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(advertising.Count == 3, "단지 광고 중개사 최대 3곳 조회");
    Assert(handler.LastRequestUri?.AbsolutePath == "/api/articles/complex/109250", "단지번호 경로 적용");
    Assert(handler.LastRequestUri?.Query.Contains("order=rank") == true, "광고 순위 정렬 적용");
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "advertising-token", "광고 Authorization 적용");
    Assert(handler.CookieHeader == "AD=test", "광고 Cookie 적용");

    var complexInfo = client.GetComplexInformationAsync("15832", "대체명", settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(complexInfo.ComplexName == "경희궁자이", "단지정보 단지명");
    Assert(complexInfo.HouseholdSummary == "589세대", "단지정보 세대수");
    Assert(string.IsNullOrEmpty(complexInfo.Error), "단지정보 오류 없음");
    Assert(handler.LastRequestUri?.AbsolutePath == "/api/complexes/15832", "단지정보 경로 적용");
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "complex-token", "단지정보 Authorization 적용");
    Assert(handler.CookieHeader == "CPLX=test", "단지정보 Cookie 적용");

    var advertisementRealtors = client.GetComplexAdvertisementRealtorsAsync("143682", settings, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(advertisementRealtors.Count == 3, "단지 광고 중개인 3명");
    Assert(advertisementRealtors[0].RealtorName == "광고1중개", "단지 광고 1순위 중개인");
    Assert(advertisementRealtors[0].RealtorId == "bizmk", "단지 광고 1순위 중개인 ID");
    Assert(handler.LastRequestUri?.Host == "fin.land.naver.com", "광고 API 절대 주소 적용");
    Assert(handler.LastRequestUri?.Query.Contains("complexNumber=143682") == true, "광고 API 단지번호 파라미터");
    Assert(handler.LastRequestUri?.Query.Contains("advertisementRerankChannelType=property.complex.price") == true, "광고 API 채널 파라미터");
    Assert(handler.CookieHeader == "FIN=test", "광고 API Cookie 적용");
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

sealed class ConcurrentEndpointHandler(int delayMilliseconds = 100) : HttpMessageHandler
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
            await Task.Delay(delayMilliseconds, cancellationToken);
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
