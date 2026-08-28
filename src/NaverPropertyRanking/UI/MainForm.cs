using System.Diagnostics;
using System.Net;
using System.Windows.Forms;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

/// <summary>중단 가능한 조회 작업의 종류. 어느 버튼이 조회중단으로 바뀔지 결정한다.</summary>
public enum CancellableOperationKind
{
    None,
    /// <summary>매물동기화 버튼(자동 주기 조회 포함).</summary>
    ListingSync,
    /// <summary>순위조회 버튼(현재 페이지·실패 재조회 포함).</summary>
    RankingRefresh
}

public sealed class MainForm : Form
{
    private readonly LocalStore _store;
    private readonly NaverLandClient _apiClient;
    private readonly ApiConfiguration _apiConfiguration;
    private readonly GoogleAuthenticationClient? _authenticationClient;
    private readonly AuthenticationSession? _authenticationSession;
    /// <summary>
    /// 로그인 세션 유지용 하트비트 타이머. 대량 조회 중에도 주기를 지켜야 하므로
    /// UI 메시지 펌프에 묶이는 WinForms 타이머가 아니라 스레드풀 타이머를 쓴다.
    /// 한 번 보낼 때마다 다음 실행을 다시 예약하는 1회성 타이머로 동작한다.
    /// </summary>
    private readonly System.Threading.Timer? _sessionHeartbeatTimer;
    /// <summary>
    /// 타이머가 깨어나는 간격. 이때마다 날짜가 바뀌었는지만 확인하고,
    /// 바뀌지 않았으면 서버에 아무것도 보내지 않는다. 절전으로 자정을 놓쳐도
    /// 깨어난 뒤 이 주기 안에 확인이 이뤄진다.
    /// </summary>
    private static readonly TimeSpan MembershipCheckPollInterval = TimeSpan.FromMinutes(20);
    /// <summary>마지막으로 서버 확인을 마친 날짜. 이 날짜가 바뀔 때만 다시 확인한다.</summary>
    private DateTime _lastMembershipCheckedDate;
    /// <summary>접속 확인이 실패했을 때 다음 주기를 기다리지 않고 재시도하는 간격.</summary>
    private static readonly TimeSpan HeartbeatRetryDelay = TimeSpan.FromSeconds(20);
    /// <summary>하트비트 1회에 허용하는 최대 대기 시간. 한 번 멈춰도 만료 시간을 다 먹지 않게 한다.</summary>
    private static readonly TimeSpan HeartbeatRequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>세션 종료가 확정돼 더 이상 접속 확인을 보내지 않는 상태.</summary>
    private bool _sessionTerminated;
    /// <summary>
    /// 창이 닫힌 뒤 로그인 화면으로 돌아가야 하는지. 멤버십 종료처럼 세션이 서버에서
    /// 닫힌 경우에 설정되며, 호출 측(Program)이 이 값을 보고 다시 로그인 창을 띄운다.
    /// </summary>
    public bool ReturnToLogin { get; private set; }
    private readonly string _currentVersion;
    private readonly TextBox _groupId = new() { Width = 150, PlaceholderText = "네이버 부동산 단체 ID" };
    private readonly TextBox _articleNumbers = new() { Width = 150, PlaceholderText = "예: 2612345678, 2612345679" };
    private readonly CheckBox _saveGroupId = new() { Text = "저장", AutoSize = true, Checked = true, Padding = new Padding(0, 6, 0, 0), Visible = false };
    private const string LoadButtonDefaultText = "매물동기화";
    private const string RefreshButtonDefaultText = "순위조회";
    private const string CancelOperationButtonText = "조회중단";
    /// <summary>랭킹 조회 중 그리드·진행 표시를 갱신하는 간격(건수).</summary>
    private readonly Button _loadButton = new() { Text = LoadButtonDefaultText, Width = 125, Height = 32 };
    private readonly Button _refreshButton = new() { Text = RefreshButtonDefaultText, Width = 100, Height = 32, Enabled = false };
    private readonly Button _retryFailedRankingsButton = new()
    {
        Text = "실패 재조회",
        Width = 110,
        Height = 32,
        Enabled = false,
        Visible = false
    };
    private readonly Button _advertisementAnalysisButton = new()
    {
        Text = "광고분석",
        Width = 95,
        Height = 32,
        Enabled = false
    };
    private readonly Button _settingsButton = new() { Text = "설정", Width = 75, Height = 32 };
    private readonly Button _logoutButton = new() { Text = "로그아웃", Width = 82, Height = 32 };
    private readonly Button _excelExportButton = new()
    {
        Text = "Excel 출력",
        Width = 112,
        Height = 30,
        Enabled = false,
        TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.FromArgb(16, 124, 65),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        Cursor = Cursors.Hand,
        Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
        FlatAppearance =
        {
            BorderSize = 0,
            MouseOverBackColor = Color.FromArgb(12, 145, 72),
            MouseDownBackColor = Color.FromArgb(9, 101, 52)
        }
    };
    /// <summary>검색조건과 Excel 출력 버튼 사이에 표시하는 진행 상태(볼드).</summary>
    private readonly Label _progressStatus = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
        ForeColor = Color.FromArgb(16, 124, 65),
        Margin = new Padding(8, 0, 8, 0)
    };
    private readonly Button _previousPageButton = new() { Text = "◀ 이전", Width = 90, Height = 30 };
    private readonly Button _nextPageButton = new() { Text = "다음 ▶", Width = 90, Height = 30 };
    private readonly Label _pageLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ComboBox _pageSizeCombo = new() { Width = 74, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _rankImmediately = new() { Text = "매물 조회 시 랭킹 바로조회", AutoSize = true };
    private readonly RadioButton _rankAscendingSort = new() { Text = "랭킹낮은순", AutoSize = true };
    private readonly RadioButton _rankDescendingSort = new() { Text = "랭킹높은순", AutoSize = true };
    private readonly RadioButton _duplicateDescendingSort = new() { Text = "동일매물많은순", AutoSize = true, Checked = true };
    private readonly RadioButton _duplicateAscendingSort = new() { Text = "동일매물적은순", AutoSize = true };
    private readonly CheckBox _excludeSingleListings = new() { Text = "단일매물 제외", AutoSize = true };
    /// <summary>단지명 필터를 여는 버튼. 선택 상태를 요약해 보여준다.</summary>
    private readonly Button _complexNameFilterButton = new()
    {
        Width = 190,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        FlatStyle = FlatStyle.System
    };
    /// <summary>단지명 다중 선택 목록. 체크한 단지만 표시한다.</summary>
    private readonly CheckedListBox _complexNameList = new()
    {
        CheckOnClick = true,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Width = 240,
        Height = 260
    };
    private ToolStripDropDown? _complexNameDropDown;
    /// <summary>체크된 단지명. 비어 있으면 전체 표시.</summary>
    private readonly HashSet<string> _selectedComplexNames = new(StringComparer.Ordinal);
    /// <summary>체크 목록에 마지막으로 채운 단지명. 구성이 그대로면 항목을 다시 만들지 않는다.</summary>
    private List<string> _complexFilterNames = [];
    private const string AllComplexNamesText = "단지 전체";
    /// <summary>목록을 다시 채우는 동안 체크 변경 이벤트로 재렌더링이 겹치지 않게 막는다.</summary>
    private bool _populatingComplexNames;
    /// <summary>헤더 클릭으로 지정한 정렬 컬럼. null이면 정렬방법 라디오 설정을 따른다.</summary>
    private string? _gridSortColumn;
    /// <summary>헤더 정렬 방향. 클릭할 때마다 내림차순 → 오름차순 → 해제로 순환한다.</summary>
    private SortOrder _gridSortDirection = SortOrder.None;
    private readonly Panel _noticePanel = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _noticeText = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(45, 45, 45),
        Padding = new Padding(7, 0, 4, 0)
    };
    private readonly VScrollBar _noticeScroll = new() { Dock = DockStyle.Right, Width = 18 };
    private string[] _notices = [];
    private readonly DataGridView _grid = new BufferedDataGridView();
    private readonly BusyProgressOverlay _busyOverlay;
    /// <summary>최근 상태 메시지. 하단 상태표시줄은 사용하지 않고 상단 진행 표시와 메시지 박스에만 쓴다.</summary>
    private string _statusText = "준비";
    /// <summary>검색조건 줄에서 진행 상태 바로 앞에 표시하는 최종조회일시.</summary>
    private readonly Label _lastChecked = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = Color.FromArgb(85, 100, 95),
        Margin = new Padding(8, 0, 4, 0)
    };
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _cooldownUiTimer = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _noticeRotationTimer = new() { Interval = 60_000 };
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>진행 중인 조회 작업 취소용. 작업 시작 시 만들고 종료 시 정리한다.</summary>
    private CancellationTokenSource? _operationCancellation;
    private bool _operationCancelledByUser;
    /// <summary>진행 중인 작업을 시작한 버튼. 해당 버튼만 조회중단으로 바뀐다.</summary>
    private CancellableOperationKind _operationKind = CancellableOperationKind.None;
    private readonly HashSet<string> _expanded = [];
    private readonly HashSet<string> _propertyAnalysisInProgress = [];
    /// <summary>열려 있는 데이터 팝업. 같은 매물의 같은 팝업이 중복 생성되지 않게 키로 관리한다.</summary>
    private readonly Dictionary<string, Form> _dataPopups = new(StringComparer.Ordinal);
    /// <summary>행 렌더링용 볼드 글꼴. 행마다 새로 만들면 GDI 핸들이 누적돼 대량 조회에서 점점 느려진다.</summary>
    private Font? _gridBoldFont;
    private readonly HashSet<string> _failedRankingArticleNumbers = [];
    /// <summary>팝업 OFF 상태에서 단독→동일생성이 발생해 최상단 고정 중인 매물.</summary>
    private readonly HashSet<string> _pendingNewDuplicates = [];
    /// <summary>팝업 OFF 상태에서 금액변동이 발생해 최상단 고정 중인 매물과 변동 내역.</summary>
    private readonly Dictionary<string, IReadOnlyList<PriceChangeDetail>> _pendingPriceChanges = new(StringComparer.Ordinal);
    private Dictionary<string, ListingSnapshot> _snapshots;
    private readonly Dictionary<string, RankingResult> _rankingCache = [];
    private List<Listing> _ownListings = [];
    private List<RankingResult> _results = [];
    private int _currentPage = 1;
    private AppSettings _settings;
    private bool _refreshing;
    private bool _reallyExit;
    private bool _hideTipShown;
    private DateTime? _lastRateLimitNoticeUntilUtc;
    private DateTime? _lastRankingCompletedUtc;
    private DateTime? _nextScheduledRefreshUtc;
    private bool _autoRetryFailedRankingsRunning;
    private readonly HashSet<string> _lastCompletedRankingTargets = [];
    private bool _hasLoadedListings;
    private readonly List<RankingNotificationForm> _notificationPopups = [];
    private bool _startingListingWorkflow;
    private string? _loadedListingLoginId;
    private string? _loadedListingGroupId;
    private DateTime? _restoredListingCacheAtUtc;
    private ListingSortOrder _listingSortOrder = ListingSortOrder.DuplicateCountDescending;
    private bool _startupCacheSynchronizationStarted;
    private bool _applyingColumnOrder;

    public MainForm(
        LocalStore store,
        NaverLandClient apiClient,
        AppSettings settings,
        ApiConfiguration apiConfiguration,
        AuthenticationSession? authenticationSession = null,
        GoogleAuthenticationClient? authenticationClient = null,
        string? currentVersion = null)
    {
        _store = store;
        _apiClient = apiClient;
        _settings = settings;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;
        _settings.StartMinimized = false;
        _apiConfiguration = apiConfiguration;
        _authenticationClient = authenticationClient;
        _authenticationSession = authenticationSession;
        _advertisementAnalysisButton.Enabled = CanUseAdvertisementAnalysis;
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion)
            ? Application.ProductVersion
            : currentVersion.Trim();
        _snapshots = store.LoadSnapshots();

        Text = BuildWindowTitle(authenticationSession);
        _applicationIcon = LoadApplicationIcon();
        Icon = _applicationIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 580);
        Size = new Size(1280, 760);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        _trayIcon = BuildTrayIcon();
        BuildLayout();
        _busyOverlay = new BusyProgressOverlay(this);
        // 로그인 없이 실행하는 구성에서는 돌아갈 로그인 화면이 없으므로 로그아웃도 숨긴다.
        _logoutButton.Visible = _authenticationSession is not null;
        WireEvents();
        ApplySettingsToUi();
        RestoreListingCache();
        PopulateNotices();
        ConfigureTimer();
        UpdateCooldownUi();
        if (_authenticationClient is not null && _authenticationSession is not null)
        {
            // 멤버십 종료일은 날짜 단위라 상태가 바뀌는 시점은 자정뿐이다.
            // 그래서 로그인 시 한 번 확인하고, 이후에는 날짜가 바뀐 뒤 한 번만 다시 확인한다.
            // 타이머는 주기적으로 깨어나지만 날짜가 그대로면 서버에 요청을 보내지 않는다.
            _lastMembershipCheckedDate = DateTime.Now.Date;
            _sessionHeartbeatTimer = new System.Threading.Timer(
                _ => _ = SendSessionHeartbeatAsync(),
                null,
                (int)MembershipCheckPollInterval.TotalMilliseconds,
                Timeout.Infinite);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var authError = GetRequiredAuthError();
        if (authError is not null)
        {
            SetStatus(authError);
        }
        else if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            SetStatus(FormatRateLimitStatus(blockedUntil));
        }
        else if (_restoredListingCacheAtUtc is { } restoredAt)
        {
            SetStatus($"로컬 매물 정보 {_ownListings.Count}건을 불러왔습니다. 저장 시각: {restoredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (!_startupCacheSynchronizationStarted)
            {
                _startupCacheSynchronizationStarted = true;
                BeginInvoke(async () => await SynchronizeRestoredListingCacheAsync());
            }
        }
        else SetStatus("단체 ID를 입력한 후 매물목록조회를 눌러 주세요.");
    }

    private void BuildLayout()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 94,
            BackColor = Color.FromArgb(247, 250, 249),
            Padding = new Padding(16, 7, 16, 7)
        };
        var searchLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = header.BackColor
        };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var titleLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = header.BackColor
        };
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var noticeTitle = new Label
        {
            Text = "공지사항",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = Padding.Empty
        };
        var version = new Label
        {
            Text = $"버전 {_currentVersion}",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(86, 103, 97),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(6, 3, 0, 0)
        };
        _noticePanel.Margin = new Padding(0, 5, 0, 5);
        _noticePanel.Controls.Add(_noticeText);
        _noticePanel.Controls.Add(_noticeScroll);
        titleLayout.Controls.Add(version, 0, 0);
        titleLayout.Controls.Add(noticeTitle, 1, 0);
        titleLayout.Controls.Add(_noticePanel, 2, 0);
        searchLayout.Controls.Add(titleLayout, 0, 0);
        searchLayout.SetColumnSpan(titleLayout, 10);

        var groupLabel = new Label
        {
            Text = "단체 ID",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty
        };
        _groupId.Dock = DockStyle.None;
        _groupId.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _groupId.Margin = new Padding(0, 0, 10, 0);
        _saveGroupId.Dock = DockStyle.None;
        _saveGroupId.Anchor = AnchorStyles.Left;
        _saveGroupId.Padding = Padding.Empty;
        _saveGroupId.Margin = Padding.Empty;

        foreach (var button in new[]
                 {
                     _loadButton, _refreshButton, _retryFailedRankingsButton,
                     _advertisementAnalysisButton, _settingsButton
                 })
        {
            button.Dock = DockStyle.None;
            button.Anchor = AnchorStyles.None;
            button.Margin = Padding.Empty;
        }

        searchLayout.Controls.Add(groupLabel, 0, 1);
        searchLayout.Controls.Add(_groupId, 1, 1);
        searchLayout.Controls.Add(_loadButton, 2, 1);
        searchLayout.Controls.Add(_refreshButton, 3, 1);
        searchLayout.Controls.Add(_advertisementAnalysisButton, 4, 1);
        searchLayout.Controls.Add(_saveGroupId, 5, 1);
        searchLayout.Controls.Add(_retryFailedRankingsButton, 6, 1);
        searchLayout.Controls.Add(_settingsButton, 8, 1);
        searchLayout.Controls.Add(_logoutButton, 9, 1);
        header.Controls.Add(searchLayout);

        ConfigureGrid();
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var sortPanel = BuildSortPanel();
        var pagingPanel = BuildPagingPanel();
        content.Controls.Add(_grid);
        content.Controls.Add(pagingPanel);
        content.Controls.Add(sortPanel);

        Controls.Add(content);
        Controls.Add(header);
    }

    private Control BuildSortPanel()
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.FromArgb(247, 250, 249)
        };
        // 검색조건은 내용 크기만큼, 남는 공간은 최종조회일시와 진행 상태 표시에 사용한다.
        container.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 8, 0, 0),
            Margin = Padding.Empty,
            BackColor = container.BackColor
        };
        optionsPanel.Controls.Add(new Label
        {
            Text = "검색조건",
            AutoSize = true,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = new Padding(0, 4, 12, 0)
        });
        _excludeSingleListings.Margin = new Padding(0, 3, 20, 0);
        optionsPanel.Controls.Add(_excludeSingleListings);
        optionsPanel.Controls.Add(new Label
        {
            Text = "단지명",
            AutoSize = true,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = new Padding(0, 4, 8, 0)
        });
        _complexNameFilterButton.Margin = new Padding(0, 1, 28, 0);
        optionsPanel.Controls.Add(_complexNameFilterButton);
        optionsPanel.Controls.Add(new Label
        {
            Text = "정렬방법",
            AutoSize = true,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = new Padding(0, 4, 16, 0)
        });
        foreach (var option in new[]
                 {
                     _rankAscendingSort,
                     _rankDescendingSort,
                     _duplicateDescendingSort,
                     _duplicateAscendingSort
                 })
        {
            option.Margin = new Padding(0, 3, 22, 0);
            optionsPanel.Controls.Add(option);
        }
        _excelExportButton.Anchor = AnchorStyles.None;
        _excelExportButton.Margin = new Padding(6, 6, 10, 6);
        // 최종조회일시를 진행 상태 바로 앞에 붙여 한 줄에서 함께 읽히게 한다.
        _lastChecked.AutoSize = true;
        container.Controls.Add(optionsPanel, 0, 0);
        container.Controls.Add(_lastChecked, 1, 0);
        container.Controls.Add(_progressStatus, 2, 0);
        container.Controls.Add(_excelExportButton, 3, 0);
        return container;
    }

    private Control BuildPagingPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 7, 0, 0),
            BackColor = Color.FromArgb(247, 250, 249)
        };
        _pageLabel.Margin = new Padding(0, 7, 24, 0);
        var help = new Label
        {
            Text = "매물을 체크한 상태로 창을 닫으면 선택 항목을 백그라운드에서 재조회합니다.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 7, 0, 0)
        };
        panel.Controls.Add(_pageLabel);
        panel.Controls.Add(help);
        UpdatePagingControls();
        return panel;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToOrderColumns = true;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.RowTemplate.Height = 30;
        _grid.ColumnHeadersHeight = 38;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Duplicates",
            HeaderText = "",
            ToolTipText = "동일매물 목록을 새 창으로 엽니다.",
            Width = 38,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Expand", HeaderText = "", Width = 42, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mine", HeaderText = "구분", Width = 65 });
        var articleNoColumn = new DataGridViewTextBoxColumn
        {
            Name = "ArticleNo",
            HeaderText = "매물번호",
            Width = 150,
            MinimumWidth = 150,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        };
        articleNoColumn.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.Columns.Add(articleNoColumn);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PropertyType", HeaderText = "매물유형", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Address", HeaderText = "매물명/소재지", Width = 280, MinimumWidth = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Trade", HeaderText = "거래유형", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "거래금액", Width = 120 });
        // 등록일은 네이버 확인일(articleConfirmYmd)을 사용한다.
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RegisteredDate", HeaderText = "등록일", Width = 84 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ComplexName", HeaderText = "단지명", Width = 150, MinimumWidth = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "QueryResult", HeaderText = "조회결과", Width = 78 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PreviousRank", HeaderText = "이전순위", Width = 82 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CurrentRank", HeaderText = "현재순위", Width = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "동일매물", Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Realtor", HeaderText = "공인중개사", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Provider", HeaderText = "CP사", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "VerificationMethod", HeaderText = "검증방식", Width = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "상태", Width = 180 });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "PropertyAnalysis",
            HeaderText = "물건분석",
            Width = 100,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Flat
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "설명",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 260
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ComplexNo",
            HeaderText = "단지번호",
            Visible = false,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        // 그리드가 스스로 행을 재배열하면 내 매물·동일매물 묶음이 흐트러진다.
        // 헤더 클릭은 받되 정렬은 직접 처리하도록 Programmatic으로 둔다.
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            if (column.SortMode == DataGridViewColumnSortMode.NotSortable) continue;
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        }
        ApplySavedGridColumnOrder();
    }

    private void ApplySavedGridColumnOrder()
    {
        var savedOrder = _settings.GridColumnOrder ?? [];
        if (savedOrder.Count == 0)
        {
            _grid.Columns["PropertyAnalysis"].DisplayIndex = _grid.Columns["Mine"].DisplayIndex;
            return;
        }

        _applyingColumnOrder = true;
        try
        {
            var nextIndex = 0;
            foreach (var columnName in savedOrder)
            {
                if (!_grid.Columns.Contains(columnName)) continue;
                _grid.Columns[columnName].DisplayIndex = nextIndex++;
            }

            foreach (var column in _grid.Columns.Cast<DataGridViewColumn>()
                         .Where(column => !savedOrder.Contains(column.Name, StringComparer.Ordinal))
                         .OrderBy(column => column.Index))
                column.DisplayIndex = nextIndex++;

            if (!savedOrder.Contains("VerificationMethod", StringComparer.Ordinal))
                _grid.Columns["VerificationMethod"].DisplayIndex =
                    _grid.Columns["Provider"].DisplayIndex + 1;

            // 이전 버전 설정에는 단지명이 없으므로 기본 위치인 등록일 다음으로 보낸다.
            if (!savedOrder.Contains("ComplexName", StringComparer.Ordinal))
                _grid.Columns["ComplexName"].DisplayIndex =
                    _grid.Columns["RegisteredDate"].DisplayIndex + 1;

            // 돋보기 열은 항상 트리 펼침 버튼 바로 앞이어야 한다.
            if (!savedOrder.Contains("Duplicates", StringComparer.Ordinal))
                _grid.Columns["Duplicates"].DisplayIndex = _grid.Columns["Expand"].DisplayIndex;

            // 저장된 순서에 없는 신규 컬럼은(기존 사용자) 기본 위치로 배치한다.
            if (!savedOrder.Contains("RegisteredDate", StringComparer.Ordinal))
                _grid.Columns["RegisteredDate"].DisplayIndex =
                    _grid.Columns["Price"].DisplayIndex + 1;
            if (!savedOrder.Contains("QueryResult", StringComparer.Ordinal))
                _grid.Columns["QueryResult"].DisplayIndex =
                    _grid.Columns["PreviousRank"].DisplayIndex;
        }
        finally
        {
            _applyingColumnOrder = false;
        }
    }

    private void SaveGridColumnOrder()
    {
        if (_applyingColumnOrder || _grid.Columns.Count == 0) return;
        var columnOrder = _grid.Columns.Cast<DataGridViewColumn>()
            .OrderBy(column => column.DisplayIndex)
            .Select(column => column.Name)
            .ToList();
        if ((_settings.GridColumnOrder ?? []).SequenceEqual(columnOrder, StringComparer.Ordinal)) return;
        _settings.GridColumnOrder = columnOrder;
        _store.SaveSettings(_settings);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("창 열기", null, (_, _) => ShowWindow());
        menu.Items.Add("지금 새로고침", null, async (_, _) => await TrayRefreshAsync());
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApplication());

        var icon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "매물분석알림",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private void WireEvents()
    {
        // 자기 버튼으로 시작한 작업이 진행 중일 때만 조회중단으로 동작한다.
        _loadButton.Click += async (_, _) =>
        {
            if (_operationKind == CancellableOperationKind.ListingSync)
            {
                CancelCurrentOperation();
                return;
            }
            if (_operationCancellation is not null) return;
            await StartListingWorkflowAsync();
        };
        _refreshButton.Click += async (_, _) =>
        {
            if (_operationKind == CancellableOperationKind.RankingRefresh)
            {
                CancelCurrentOperation();
                return;
            }
            if (_operationCancellation is not null) return;
            await RefreshAllRankingsAsync(false);
        };
        _retryFailedRankingsButton.Click += async (_, _) => await RetryFailedRankingsAsync();
        _advertisementAnalysisButton.Click += (_, _) => ShowOwnedComplexListPopup();
        _settingsButton.Click += (_, _) => OpenSettings();
        _logoutButton.Click += (_, _) => Logout();
        _excelExportButton.Click += (_, _) => ExportCurrentListToExcel();
        _timer.Tick += async (_, _) =>
        {
            _nextScheduledRefreshUtc = DateTime.UtcNow.AddMilliseconds(_timer.Interval);
            if (_hasLoadedListings) await RefreshAllAsync(true);
        };
        _cooldownUiTimer.Tick += (_, _) => UpdateCooldownUi();
        _noticeRotationTimer.Tick += (_, _) => AdvanceNotice();
        _grid.CellClick += GridOnCellClick;
        _grid.ColumnDisplayIndexChanged += (_, _) => SaveGridColumnOrder();
        _grid.CellDoubleClick += GridOnCellDoubleClick;
        _grid.KeyDown += GridOnKeyDown;
        _noticeScroll.Scroll += (_, _) => ShowNotice(_noticeScroll.Value);
        _noticeText.MouseWheel += NoticeOnMouseWheel;
        _noticePanel.MouseWheel += NoticeOnMouseWheel;
        _rankAscendingSort.CheckedChanged += (_, _) => ApplyListingSort(
            _rankAscendingSort,
            ListingSortOrder.RankAscending);
        _rankDescendingSort.CheckedChanged += (_, _) => ApplyListingSort(
            _rankDescendingSort,
            ListingSortOrder.RankDescending);
        _duplicateDescendingSort.CheckedChanged += (_, _) => ApplyListingSort(
            _duplicateDescendingSort,
            ListingSortOrder.DuplicateCountDescending);
        _duplicateAscendingSort.CheckedChanged += (_, _) => ApplyListingSort(
            _duplicateAscendingSort,
            ListingSortOrder.DuplicateCountAscending);
        _excludeSingleListings.CheckedChanged += (_, _) => ApplyListingVisibilityFilter();
        _complexNameFilterButton.Click += (_, _) => ShowComplexNameFilter();
        // 여러 단지를 연달아 체크하는 동안 매번 다시 그리면 느리므로 목록을 닫을 때 한 번만 적용한다.
        _complexNameList.ItemCheck += (_, e) =>
        {
            if (_populatingComplexNames) return;
            if (e.Index == 0) BeginInvoke(() => SetAllComplexNamesChecked(e.NewValue == CheckState.Checked));
        };
        _grid.ColumnHeaderMouseClick += GridOnColumnHeaderClick;
        FormClosing += OnFormClosing;
    }

    private void ApplyListingVisibilityFilter()
    {
        _currentPage = 1;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        var visibleCount = VisibleListings().Count;
        if (_selectedComplexNames.Count > 0)
        {
            SetStatus($"단지명 필터 {_selectedComplexNames.Count}곳 적용 · " +
                      $"표시 {visibleCount}건 / 전체 {_ownListings.Count}건");
            return;
        }
        SetStatus(_excludeSingleListings.Checked
            ? $"단일매물을 제외했습니다. 표시 {visibleCount}건 / 전체 {_ownListings.Count}건"
            : $"전체 {_ownListings.Count}건 표시");
    }

    /// <summary>
    /// 컬럼 헤더를 누를 때마다 내림차순 → 오름차순 → 정렬해제로 순환한다.
    /// 다른 컬럼을 누르면 그 컬럼의 내림차순부터 다시 시작한다.
    /// </summary>
    private void GridOnColumnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0) return;
        var column = _grid.Columns[e.ColumnIndex];
        if (column.Name is "Expand" or "PropertyAnalysis" or "Duplicates") return;

        _gridSortDirection = _gridSortColumn == column.Name
            ? _gridSortDirection switch
            {
                SortOrder.Descending => SortOrder.Ascending,
                SortOrder.Ascending => SortOrder.None,
                _ => SortOrder.Descending
            }
            : SortOrder.Descending;
        _gridSortColumn = _gridSortDirection == SortOrder.None ? null : column.Name;

        _currentPage = 1;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        SetStatus(_gridSortColumn is null
            ? "정렬을 해제했습니다."
            : $"'{column.HeaderText}' {(_gridSortDirection == SortOrder.Descending ? "내림차순" : "오름차순")} 정렬");
    }

    /// <summary>정렬 중인 컬럼 헤더에 방향 표시를 남기고 나머지는 지운다.</summary>
    private void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = column.Name == _gridSortColumn
                ? _gridSortDirection
                : SortOrder.None;
        }
    }

    /// <summary>
    /// 헤더 정렬이 지정돼 있으면 그 컬럼 값으로 정렬한다.
    /// 순위·동일매물처럼 숫자인 컬럼은 숫자로, 나머지는 표시 문자열로 비교한다.
    /// </summary>
    private List<Listing> ApplyGridSort(List<Listing> listings)
    {
        if (_gridSortColumn is null || _gridSortDirection == SortOrder.None) return listings;

        var descending = _gridSortDirection == SortOrder.Descending;
        return _gridSortColumn switch
        {
            "PreviousRank" => SortByNumber(listings, listing => RankingOf(listing)?.PreviousRank, descending),
            "CurrentRank" => SortByNumber(listings, listing => RankingOf(listing)?.Rank, descending),
            "Total" => SortByNumber(listings, listing => RankingOf(listing)?.Total ?? 0, descending),
            _ => SortByText(listings, listing => GridSortText(_gridSortColumn, listing), descending)
        };
    }

    private RankingResult? RankingOf(Listing listing) =>
        _rankingCache.TryGetValue(listing.ArticleNo, out var result) ? result : null;

    private static List<Listing> SortByNumber(
        List<Listing> listings,
        Func<Listing, int?> key,
        bool descending)
    {
        // 값이 없는 매물은 방향과 무관하게 항상 뒤로 보낸다.
        var ordered = listings.OrderBy(listing => key(listing) is null);
        return (descending
                ? ordered.ThenByDescending(listing => key(listing) ?? 0)
                : ordered.ThenBy(listing => key(listing) ?? 0))
            .ToList();
    }

    private static List<Listing> SortByText(
        List<Listing> listings,
        Func<Listing, string> key,
        bool descending)
    {
        var ordered = listings.OrderBy(listing => string.IsNullOrWhiteSpace(key(listing)));
        return (descending
                ? ordered.ThenByDescending(key, StringComparer.CurrentCulture)
                : ordered.ThenBy(key, StringComparer.CurrentCulture))
            .ToList();
    }

    private string GridSortText(string columnName, Listing listing) => columnName switch
    {
        "Mine" => listing.IsMine ? "내 매물" : "동일매물",
        "ArticleNo" => listing.ArticleNo,
        "PropertyType" => PropertyTypeDisplay(listing),
        "Address" => ListingNameDisplay(listing),
        "Trade" => listing.TradeType,
        "Price" => listing.Price,
        "RegisteredDate" => RegistrationDateDisplay(listing.RegisteredDate),
        "ComplexName" => ComplexNameDisplay(listing),
        "QueryResult" => RankingOf(listing) is { } result ? QueryResultDisplay(result) : string.Empty,
        "Realtor" => listing.RealtorName,
        "Provider" => listing.ProviderName,
        "VerificationMethod" => VerificationTypeFormatter.Format(listing.VerificationTypeCode),
        "Description" => listing.Description,
        _ => string.Empty
    };

    /// <summary>단지명 체크 목록을 버튼 아래에 펼친다.</summary>
    private void ShowComplexNameFilter()
    {
        _complexNameDropDown ??= new ToolStripDropDown
        {
            AutoClose = true,
            DropShadowEnabled = true,
            Padding = Padding.Empty,
            Items = { new ToolStripControlHost(_complexNameList) { Margin = Padding.Empty, Padding = Padding.Empty } }
        };
        // 체크를 여러 번 하는 동안은 그대로 두고, 목록을 닫을 때 한 번만 화면에 반영한다.
        _complexNameDropDown.Closed -= ComplexNameFilterClosed;
        _complexNameDropDown.Closed += ComplexNameFilterClosed;
        _complexNameDropDown.Show(
            _complexNameFilterButton,
            new Point(0, _complexNameFilterButton.Height));
    }

    private void ComplexNameFilterClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        var checkedNames = _complexNameList.CheckedItems
            .Cast<string>()
            .Where(name => name != AllComplexNamesText)
            .ToHashSet(StringComparer.Ordinal);

        // 전부 체크한 것은 전체 표시와 같으므로 필터를 비운 상태로 본다.
        if (checkedNames.Count == _complexNameList.Items.Count - 1) checkedNames.Clear();
        if (checkedNames.SetEquals(_selectedComplexNames))
        {
            UpdateComplexNameFilterButton();
            return;
        }

        _selectedComplexNames.Clear();
        foreach (var name in checkedNames) _selectedComplexNames.Add(name);
        ApplyListingVisibilityFilter();
    }

    /// <summary>'단지 전체' 항목으로 나머지 항목을 한 번에 켜고 끈다.</summary>
    private void SetAllComplexNamesChecked(bool isChecked)
    {
        _populatingComplexNames = true;
        try
        {
            for (var index = 1; index < _complexNameList.Items.Count; index++)
                _complexNameList.SetItemChecked(index, isChecked);
        }
        finally
        {
            _populatingComplexNames = false;
        }
    }

    /// <summary>선택 상태를 버튼 글자로 요약한다.</summary>
    private void UpdateComplexNameFilterButton()
    {
        _complexNameFilterButton.Text = _selectedComplexNames.Count switch
        {
            0 => AllComplexNamesText,
            1 => _selectedComplexNames.First(),
            _ => $"{_selectedComplexNames.OrderBy(name => name, StringComparer.CurrentCulture).First()} 외 " +
                 $"{_selectedComplexNames.Count - 1}곳"
        };
    }

    /// <summary>
    /// 현재 매물의 단지명으로 체크 목록을 맞춘다.
    /// 단지 구성이 바뀌었을 때만 항목을 다시 만들고, 그 외에는 체크 상태만 동기화한다.
    /// 조회를 시작하며 목록을 비운 순간에는 아무것도 하지 않는다.
    /// 그 상태에서 갱신하면 사용자가 걸어 둔 필터가 지워지기 때문이다.
    /// </summary>
    private void PopulateComplexNameFilter()
    {
        if (_ownListings.Count == 0) return;

        var names = _ownListings
            .Select(ComplexNameDisplay)
            .Where(name => name != "-")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToList();
        var namesChanged = !names.SequenceEqual(_complexFilterNames, StringComparer.Ordinal);
        if (namesChanged)
        {
            _complexFilterNames = names;
            // 이번 조회에서 사라진 단지만 선택에서 뺀다.
            _selectedComplexNames.RemoveWhere(name => !names.Contains(name, StringComparer.Ordinal));
        }

        _populatingComplexNames = true;
        try
        {
            if (namesChanged)
            {
                _complexNameList.Items.Clear();
                _complexNameList.Items.Add(AllComplexNamesText, _selectedComplexNames.Count == 0);
                foreach (var name in names)
                    _complexNameList.Items.Add(name, _selectedComplexNames.Contains(name));
            }
            else
            {
                SetComplexItemChecked(0, _selectedComplexNames.Count == 0);
                for (var index = 0; index < names.Count; index++)
                    SetComplexItemChecked(index + 1, _selectedComplexNames.Contains(names[index]));
            }
        }
        finally
        {
            _populatingComplexNames = false;
        }
        UpdateComplexNameFilterButton();
    }

    private void SetComplexItemChecked(int index, bool isChecked)
    {
        if (index >= _complexNameList.Items.Count) return;
        if (_complexNameList.GetItemChecked(index) == isChecked) return;
        _complexNameList.SetItemChecked(index, isChecked);
    }

    private void ApplyListingSort(RadioButton option, ListingSortOrder sortOrder)
    {
        if (!option.Checked) return;
        _listingSortOrder = sortOrder;
        // 정렬방법을 고르면 헤더 클릭 정렬은 해제해 기준이 하나만 남게 한다.
        _gridSortColumn = null;
        _gridSortDirection = SortOrder.None;
        _currentPage = 1;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        SetStatus($"매물 정렬을 '{option.Text}'으로 변경했습니다.");
    }

    private async Task StartListingWorkflowAsync()
    {
        if (_refreshing || _startingListingWorkflow) return;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        if (string.IsNullOrWhiteSpace(_settings.GroupId))
        {
            const string message = "단체 ID를 입력하세요.";
            SetStatus(message);
            MessageBox.Show(this, message, "입력 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        //if (!ShowSettingsDialog()) return;
        _startingListingWorkflow = true;
        SetListingLoadBusy(true);
        SetStatus("회원_단체 정보를 확인하는 중...");
        try
        {
            if (MessageBox.Show(this, "단체 ID " + _settings.GroupId + "의 매물 동기화를 진행하시겠습니까?", "확인", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.No)
                return;

            if (!await EnsureMemberGroupAsync(_settings.GroupId)) return;
        }
        finally
        {
            _startingListingWorkflow = false;
            SetListingLoadBusy(false);
        }
        await RefreshAllAsync(false, replaceExisting: true);
    }

    private async Task TrayRefreshAsync()
    {
        if (!_hasLoadedListings)
        {
            ShowWindow();
            SetStatus("단체 ID 입력 후 매물목록조회를 먼저 실행해 주세요.");
            return;
        }
        await RefreshAllRankingsAsync(false);
    }

    private void GridOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control || e.KeyCode != Keys.C || _grid.CurrentCell is null) return;

        var value = _grid.CurrentCell.FormattedValue?.ToString() ?? string.Empty;
        try
        {
            Clipboard.SetText(value);
            SetStatus($"선택한 셀 값을 복사했습니다: {value}");
        }
        catch (Exception ex)
        {
            SetStatus($"클립보드 복사 실패: {ex.Message}");
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private async Task RefreshAllAsync(bool isAutomatic, bool replaceExisting = false)
    {
        if (_refreshing) return;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.RankImmediatelyAfterListingLoad = true;
        _settings.DisplayPageSize = 0;
        if (string.IsNullOrWhiteSpace(_settings.GroupId))
        {
            SetStatus("단체 ID를 입력하세요.");
            if (!isAutomatic) MessageBox.Show(this, _statusText, "입력 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var authError = GetRequiredAuthError();
        if (authError is not null)
        {
            SetStatus(authError);
            if (!isAutomatic)
                MessageBox.Show(this, authError, "인증 설정 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_settings.RateLimitBlockedUntilUtc is { } rateLimitUntil && rateLimitUntil > DateTime.UtcNow)
        {
            HandleRateLimit(rateLimitUntil);
            return;
        }

        if (!replaceExisting && HasDisplayedListingsForCurrentIdentity())
        {
            await SynchronizeDisplayedListingsAsync(isAutomatic);
            return;
        }

        _refreshing = true;
        BeginCancellableOperation(CancellableOperationKind.ListingSync);
        SetListingLoadBusy(true);
        SetStatus("내 매물 목록을 불러오는 중...");
        try
        {
            _store.SaveSettings(_settings);
            if (replaceExisting)
            {
                foreach (var articleNo in _ownListings.Select(listing => listing.ArticleNo))
                    _snapshots.Remove(articleNo);
                _store.SaveSnapshots(_snapshots);
                _store.RemoveListingCache(CurrentLoginId(), _settings.GroupId);
            }
            _ownListings = [];
            _rankingCache.Clear();
            _expanded.Clear();
            _propertyAnalysisInProgress.Clear();
            _failedRankingArticleNumbers.Clear();
            _lastCompletedRankingTargets.Clear();
            _lastRankingCompletedUtc = null;
            _results = [];
            _currentPage = 1;
            _hasLoadedListings = false;
            _loadedListingLoginId = null;
            _loadedListingGroupId = null;
            _lastChecked.Text = string.Empty;
            RenderGrid();

            var listingProgress = new Progress<ListingLoadProgress>(progress =>
                SetStatus($"매물 목록 {progress.ListingCount}건 수집 중 · {progress.Page}페이지"));
            var loadedListings = await _apiClient.GetOwnListingsAsync(
                _settings,
                OperationToken,
                listingProgress);
            _ownListings = loadedListings
                .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            _hasLoadedListings = _ownListings.Count > 0;

            if (_ownListings.Count == 0)
            {
                SetLoadedListingIdentity(_settings.GroupId);
                SaveCurrentListingCache();
                RenderGrid();
                SetStatus("조회된 내 매물이 없습니다. 단체 ID와 인증값을 확인하세요.");
                return;
            }

            // 사용자 매물 전체 목록을 먼저 표시한 뒤 랭킹은 최대 5건 병렬 조회한다.
            UpdateCurrentPageResults("랭킹 조회 대기");
            RenderGrid();
            SetStatus(ListingProgressFormatter.Format(0, _ownListings.Count));
            await Task.Yield();
            SetLoadedListingIdentity(_settings.GroupId);
            var scope = $"전체 {_ownListings.Count}건";
            var rankingSummary = await RankListingsCoreAsync(
                _ownListings,
                true,
                scope,
                false);
            SaveCurrentListingCache();
            var progress = ListingProgressFormatter.Format(
                rankingSummary.SuccessCount + rankingSummary.FailureCount,
                _ownListings.Count);
            SetStatus(rankingSummary.FailureCount == 0
                ? $"완료 · {progress}"
                : $"{progress} · 성공 {rankingSummary.SuccessCount}건 / 실패 {rankingSummary.FailureCount}건");
            ShowRankingCompletionPopup(
                scope,
                rankingSummary.SuccessCount,
                rankingSummary.FailureCount,
                rankingSummary.Events);
            EnsureAutoRetryFailedRankingsLoop();
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (NaverApiException ex)
        {
            _store.SaveSettings(_settings);
            SetStatus(ex.Message);
            if (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                HandleRateLimit(_settings.RateLimitBlockedUntilUtc ?? DateTime.UtcNow.AddMinutes(30));
            }
            else if (!isAutomatic)
            {
                MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"조회 실패: {ex.Message}");
            if (!isAutomatic) MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            EndCancellableOperation();
            SetListingLoadBusy(false);
        }
    }

    private async Task RefreshCurrentPageAsync(bool isAutomatic, bool forceRefresh)
    {
        if (_refreshing) return;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;
        if (_ownListings.Count == 0)
        {
            SetStatus("매물목록조회를 먼저 실행해 주세요.");
            return;
        }

        var authError = NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
        if (authError is not null)
        {
            SetStatus(authError);
            if (!isAutomatic)
                MessageBox.Show(this, authError, "인증 설정 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        _refreshing = true;
        BeginCancellableOperation(CancellableOperationKind.RankingRefresh);
        SetBusy(true);
        try
        {
            await RankCurrentPageCoreAsync(forceRefresh);
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (Exception ex)
        {
            SetStatus($"랭킹 조회 실패: {ex.Message}");
            if (!isAutomatic) MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            EndCancellableOperation();
            SetBusy(false);
        }
    }

    private async Task RefreshAllRankingsAsync(bool isAutomatic)
    {
        if (_refreshing) return;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;
        if (_ownListings.Count == 0)
        {
            SetStatus("매물목록조회를 먼저 실행해 주세요.");
            return;
        }

        var authError = NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
        if (authError is not null)
        {
            SetStatus(authError);
            if (!isAutomatic)
                MessageBox.Show(this, authError, "인증 설정 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        var scope = $"전체 {_ownListings.Count}건";

        _refreshing = true;
        BeginCancellableOperation(CancellableOperationKind.RankingRefresh);
        SetBusy(true);
        try
        {
            await RankListingsCoreAsync(_ownListings, true, scope);
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (Exception ex)
        {
            SetStatus($"랭킹 조회 실패: {ex.Message}");
            if (!isAutomatic) MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            EndCancellableOperation();
            SetBusy(false);
        }
    }

    private async Task RetryFailedRankingsAsync()
    {
        if (_refreshing) return;

        var targets = _ownListings
            .Where(listing => _failedRankingArticleNumbers.Contains(listing.ArticleNo))
            .ToList();
        if (targets.Count == 0)
        {
            UpdateRetryFailedRankingsButton();
            SetStatus("재조회할 랭킹 실패 매물이 없습니다.");
            return;
        }

        var authError = NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
        if (authError is not null)
        {
            SetStatus(authError);
            MessageBox.Show(this, authError, "인증 설정 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        _refreshing = true;
        BeginCancellableOperation(CancellableOperationKind.RankingRefresh);
        SetBusy(true);
        try
        {
            var scope = $"실패 매물 재조회 {targets.Count}건";
            var summary = await RankListingsCoreAsync(targets, true, scope, false);
            var remainingFailures = _failedRankingArticleNumbers.Count(articleNo =>
                _ownListings.Any(listing => listing.ArticleNo == articleNo));
            SetStatus($"완료: {scope} · 성공 {summary.SuccessCount}건 / 실패 {summary.FailureCount}건 · 남은 실패 {remainingFailures}건");
            ShowRankingCompletionPopup(scope, summary.SuccessCount, summary.FailureCount, summary.Events);
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (Exception ex)
        {
            SetStatus($"실패 매물 재조회 오류: {ex.Message}");
            MessageBox.Show(this, ex.Message, "재조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            EndCancellableOperation();
            SetBusy(false);
        }
    }

    /// <summary>
    /// 매물동기화(또는 자동 주기 조회) 직후 랭킹 조회에 실패한 매물이 남아 있으면
    /// 다음 예정 조회 시각 전까지 실패 매물만 병렬로 반복 재조회한다.
    /// 이미 실행 중이면 중복 실행하지 않고, 각 회차 결과는 즉시 목록/그리드에 반영된다.
    /// </summary>
    private void EnsureAutoRetryFailedRankingsLoop()
    {
        if (_autoRetryFailedRankingsRunning) return;
        _autoRetryFailedRankingsRunning = true;
        _ = AutoRetryFailedRankingsLoopAsync();
    }

    private async Task AutoRetryFailedRankingsLoopAsync()
    {
        try
        {
            var attempt = 0;
            var backoff = TimeSpan.FromSeconds(5);

            while (!_lifetime.IsCancellationRequested)
            {
                var remainingFailures = _failedRankingArticleNumbers.Count(articleNo =>
                    _ownListings.Any(listing => listing.ArticleNo == articleNo));
                if (remainingFailures == 0) return;

                if (_nextScheduledRefreshUtc is { } deadline && DateTime.UtcNow >= deadline)
                {
                    SetStatus($"다음 조회 시간이 되어 실패 매물 {remainingFailures}건은 다음 주기에 재조회합니다.");
                    return;
                }
                if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow) return;
                if (NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking) is not null) return;

                if (_refreshing)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
                    continue;
                }

                var targets = _ownListings
                    .Where(listing => _failedRankingArticleNumbers.Contains(listing.ArticleNo))
                    .ToList();
                if (targets.Count == 0) return;

                attempt++;
                RankingBatchSummary summary;
                var cancelledByUser = false;
                _refreshing = true;
                BeginCancellableOperation(CancellableOperationKind.RankingRefresh);
                SetBusy(true);
                try
                {
                    var scope = $"실패 매물 자동 재조회 {attempt}회차 {targets.Count}건";
                    summary = await RankListingsCoreAsync(targets, true, scope, false);
                }
                catch (OperationCanceledException)
                {
                    HandleOperationCancelled();
                    return;
                }
                finally
                {
                    _refreshing = false;
                    cancelledByUser = _operationCancelledByUser;
                    EndCancellableOperation();
                    SetBusy(false);
                }

                // 사용자가 조회중단을 누르면 자동 재조회 반복도 멈춘다.
                if (cancelledByUser) return;

                var stillFailing = _failedRankingArticleNumbers.Count(articleNo =>
                    _ownListings.Any(listing => listing.ArticleNo == articleNo));
                if (stillFailing == 0)
                {
                    // 실패 건수가 0이 된 시점부터 재조회 설정 주기를 새로 시작한다.
                    ConfigureTimer();
                    SetStatus(_nextScheduledRefreshUtc is { } nextRefreshUtc
                        ? $"실패 매물 자동 재조회 완료 · 남은 실패 없음 · 다음 자동 조회 {nextRefreshUtc.ToLocalTime():HH:mm}"
                        : "실패 매물 자동 재조회 완료 · 남은 실패 없음");
                    return;
                }
                if (_settings.RateLimitBlockedUntilUtc is { } rateLimitUntil && rateLimitUntil > DateTime.UtcNow)
                    return;

                // 계속 실패하는 경우 네이버 서버에 요청이 몰리지 않도록 대기 시간을 점진적으로 늘린다.
                backoff = summary.SuccessCount == 0
                    ? TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60))
                    : TimeSpan.FromSeconds(5);

                await Task.Delay(backoff, _lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // App is closing or the wait was cancelled.
        }
        catch (Exception ex)
        {
            SetStatus($"실패 매물 자동 재조회 중단: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            SetBusy(false);
            _autoRetryFailedRankingsRunning = false;
        }
    }

    private async Task ChangePageAsync(int offset)
    {
        if (_refreshing || _ownListings.Count == 0) return;
        var targetPage = Math.Clamp(_currentPage + offset, 1, PageCount);
        if (targetPage == _currentPage) return;

        _currentPage = targetPage;
        _expanded.Clear();
        UpdatePagingControls();
        var pageListings = CurrentPageListings();
        if (pageListings.All(listing => _rankingCache.ContainsKey(listing.ArticleNo)))
        {
            UpdateCurrentPageResults("랭킹 미조회");
            RenderGrid();
            SetStatus($"캐시 표시: {_currentPage}/{PageCount}페이지 {_results.Count}건 · 전체 {_ownListings.Count}건");
            return;
        }
        UpdateCurrentPageResults("랭킹 조회 대기");
        RenderGrid();
        await RefreshCurrentPageAsync(false, false);
    }

    private void PageSizeComboOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        _settings.DisplayPageSize = SelectedPageSize();
        _currentPage = 1;
        _expanded.Clear();
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        _store.SaveSettings(_settings);
        SetStatus($"표시 행수를 {(_settings.DisplayPageSize == 0 ? "전체" : $"{_settings.DisplayPageSize}건")}로 변경했습니다.");
    }

    private int SelectedPageSize()
    {
        return 0;
    }

    private Task<RankingBatchSummary> RankCurrentPageCoreAsync(bool forceRefresh) =>
        RankListingsCoreAsync(CurrentPageListings(), forceRefresh, $"{_currentPage}/{PageCount}페이지");

    private async Task<RankingBatchSummary> RankListingsCoreAsync(
        IReadOnlyList<Listing> targetListings,
        bool forceRefresh,
        string scope,
        bool showCompletionPopup = true)
    {
        var ownNumbers = _ownListings.Select(x => x.ArticleNo).ToHashSet();
        var allEvents = new List<NotificationEvent>();
        var attemptedResults = new List<RankingResult>();
        var requestedListings = ListingSorter
            .Sort(targetListings, _rankingCache, _listingSortOrder)
            .Where(listing => forceRefresh || !_rankingCache.ContainsKey(listing.ArticleNo))
            .ToList();
        var pendingListings = new Queue<Listing>(requestedListings);
        var requestCount = pendingListings.Count;
        var completedCount = 0;
        UpdateCurrentPageResults("랭킹 조회 대기");
        RenderGrid();
        ConfigureBusyProgress(requestCount);

        var runningRequests = new Dictionary<Task<RankingResult>, Listing>();
        var previousRanks = new Dictionary<string, int?>(StringComparer.Ordinal);
        var stopLaunching = false;
        var operationToken = OperationToken;

        void LaunchAvailableRequests()
        {
            // 조회중단을 누르면 남은 매물은 더 이상 요청하지 않는다.
            while (!stopLaunching &&
                   !operationToken.IsCancellationRequested &&
                   runningRequests.Count < 5 &&
                   pendingListings.Count > 0)
            {
                var listing = pendingListings.Dequeue();
                int? previousRank = null;
                if (_rankingCache.TryGetValue(listing.ArticleNo, out var cached) && cached.Success)
                    previousRank = cached.Rank;
                else if (_snapshots.TryGetValue(listing.ArticleNo, out var savedSnapshot))
                    previousRank = savedSnapshot.Rank;

                previousRanks[listing.ArticleNo] = previousRank;
                var request = _apiClient.GetRankingAsync(listing, ownNumbers, _settings, OperationToken);
                runningRequests.Add(request, listing);
            }
        }

        LaunchAvailableRequests();
        while (runningRequests.Count > 0)
        {
            await Task.WhenAny(runningRequests.Keys);
            var completedTasks = runningRequests.Keys
                .Where(task => task.IsCompleted)
                .ToList();

            foreach (var completedTask in completedTasks)
            {
                var listing = runningRequests[completedTask];
                runningRequests.Remove(completedTask);
                RankingResult completedResult;
                try
                {
                    completedResult = await completedTask;
                }
                catch (OperationCanceledException)
                {
                    // 조회중단으로 취소된 요청은 성공·실패 어느 쪽으로도 집계하지 않는다.
                    continue;
                }
                completedCount++;

                var result = completedResult with
                {
                    PreviousRank = previousRanks[listing.ArticleNo]
                };
                result = SynchronizeOwnListingDetails(result);
                attemptedResults.Add(result);
                _rankingCache[listing.ArticleNo] = result;

                if (result.Success)
                {
                    _snapshots.TryGetValue(listing.ArticleNo, out var previous);
                    var comparison = RankingAnalyzer.Compare(result, previous, _settings);
                    TrackHighlightedChanges(result, previous);
                    _snapshots[listing.ArticleNo] = comparison.Snapshot;
                    allEvents.AddRange(comparison.Events);
                }
                if (result.Error?.Contains("429", StringComparison.Ordinal) == true)
                    stopLaunching = true;
            }

            // 조회 중에는 진행 건수 텍스트만 갱신한다. 대량 조회에서 그리드를 주기적으로
            // 다시 그리는 것이 가장 큰 병목이라 결과 반영은 조회가 끝난 뒤 한 번만 한다.
            SetProgressStatus($"진행중... {completedCount}/{requestCount}");
            LaunchAvailableRequests();
        }

        if (operationToken.IsCancellationRequested)
        {
            // 중단 시점까지 받은 결과는 저장하되, 남은 매물은 실패로 기록하지 않는다.
            // 실패로 남기면 자동 재조회가 다시 돌면서 중단 의도를 무시하게 된다.
            UpdateCurrentPageResults("랭킹 미조회");
            _store.SaveSnapshots(_snapshots);
            _store.SaveSettings(_settings);
            UpdateRetryFailedRankingsButton();
            RenderGrid();
            SaveCurrentListingCache();
            throw new OperationCanceledException(operationToken);
        }

        var attemptedResultsByArticleNo = attemptedResults
            .GroupBy(result => result.OwnListing.ArticleNo, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var listing in requestedListings)
        {
            if (attemptedResultsByArticleNo.TryGetValue(listing.ArticleNo, out var result) && result.Success)
                _failedRankingArticleNumbers.Remove(listing.ArticleNo);
            else
                _failedRankingArticleNumbers.Add(listing.ArticleNo);
        }
        UpdateRetryFailedRankingsButton();

        UpdateCurrentPageResults("랭킹 미조회");
        _store.SaveSnapshots(_snapshots);
        _store.SaveSettings(_settings);
        RenderGrid();
        SaveCurrentListingCache();
        _lastChecked.Text = $"최종조회일시: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        var targetResults = targetListings
            .Where(listing => _rankingCache.ContainsKey(listing.ArticleNo))
            .Select(listing => _rankingCache[listing.ArticleNo])
            .ToList();
        var failures = targetResults.Count(x => !x.Success);
        if (failures == 0 && targetResults.Count == targetListings.Count)
        {
            _lastCompletedRankingTargets.Clear();
            foreach (var listing in targetListings)
                _lastCompletedRankingTargets.Add(listing.ArticleNo);
            _lastRankingCompletedUtc = DateTime.UtcNow;
        }
        var attemptedSuccesses = attemptedResults.Count(result => result.Success);
        var attemptedFailures = Math.Max(0, requestCount - attemptedSuccesses);
        SetStatus(attemptedFailures == 0
            ? $"완료: {scope} 랭킹 조회"
            : $"완료: {scope} · 성공 {attemptedSuccesses}건 / 실패 {attemptedFailures}건");
        if (requestCount > 0 && showCompletionPopup)
            ShowRankingCompletionPopup(scope, attemptedSuccesses, attemptedFailures, allEvents);
        // 방금 받은 결과로 팝업을 맞춘다. 팝업이 API를 다시 부르지는 않는다.
        if (requestCount > 0) UpdateDataPopupsFromListings();

        var summary = new RankingBatchSummary(
            attemptedSuccesses,
            attemptedFailures,
            allEvents);

        if (_settings.RateLimitBlockedUntilUtc is { } rateLimitUntil && rateLimitUntil > DateTime.UtcNow)
        {
            HandleRateLimit(rateLimitUntil);
            return summary;
        }

        return summary;
    }

    private async Task SynchronizeRestoredListingCacheAsync()
    {
        if (_refreshing || _ownListings.Count == 0) return;

        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;

        var authError = GetRequiredAuthError();
        if (authError is not null)
        {
            SetStatus($"로컬 매물은 표시했지만 자동 동기화를 시작할 수 없습니다: {authError}");
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        // 단체 ID가 있으면 바로 조회하지 않고 사용자에게 동기화 여부를 먼저 묻는다.
        if (!string.IsNullOrWhiteSpace(_settings.GroupId))
        {
            var answer = MessageBox.Show(
                this,
                $"저장된 매물 {_ownListings.Count}건을 표시했습니다.\n" +
                $"단체 ID: {_settings.GroupId}\n\n" +
                "지금 즉시 동기화하시겠습니까?",
                "매물 동기화",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                SetStatus($"로컬 매물 {_ownListings.Count}건 표시 완료 · 동기화하려면 매물동기화를 눌러 주세요.");
                return;
            }
        }

        SetStatus($"로컬 매물 {_ownListings.Count}건 표시 완료 · 매물목록조회 기능을 자동 실행합니다.");
        if (!await EnsureMemberGroupAsync(_settings.GroupId)) return;

        // 시작 시에도 사용자가 같은 단체 ID로 매물목록조회를 누른 것과 동일하게 처리한다.
        // 최신 목록을 먼저 반영한 다음 갱신된 전체 목록의 순위를 조회한다.
        await SynchronizeDisplayedListingsAsync(true);
    }

    private bool HasDisplayedListingsForCurrentIdentity()
    {
        if (_ownListings.Count == 0 ||
            string.IsNullOrWhiteSpace(_loadedListingLoginId) ||
            string.IsNullOrWhiteSpace(_loadedListingGroupId)) return false;

        return string.Equals(
                   _loadedListingLoginId,
                   CurrentLoginId(),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   _loadedListingGroupId,
                   _settings.GroupId.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task SynchronizeDisplayedListingsAsync(bool isAutomatic)
    {
        _refreshing = true;
        BeginCancellableOperation(CancellableOperationKind.ListingSync);
        SetListingLoadBusy(true);
        SetStatus($"현재 매물 {_ownListings.Count}건을 기준으로 최신 매물목록을 다시 조회합니다.");

        try
        {
            _store.SaveSettings(_settings);
            var listingProgress = new Progress<ListingLoadProgress>(progress =>
                SetStatus($"매물목록 재조회 중 · 최신 목록 {progress.ListingCount}건 · {progress.Page}페이지"));
            var latestListings = await _apiClient.GetOwnListingsAsync(
                _settings,
                OperationToken,
                listingProgress);

            var reconciliation = ListingCollectionMerger.Reconcile(_ownListings, latestListings);
            var removedNumbers = reconciliation.RemovedListings
                .Select(listing => listing.ArticleNo)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var articleNo in removedNumbers)
            {
                _rankingCache.Remove(articleNo);
                _snapshots.Remove(articleNo);
                _expanded.Remove(articleNo);
                _failedRankingArticleNumbers.Remove(articleNo);
                _lastCompletedRankingTargets.Remove(articleNo);
            }

            _ownListings = reconciliation.Listings.ToList();
            _hasLoadedListings = _ownListings.Count > 0;
            var ownNumbers = _ownListings
                .Select(listing => listing.ArticleNo)
                .ToHashSet(StringComparer.Ordinal);
            NormalizeLoadedRankingOwnership(ownNumbers);
            RefreshRankingOwnListingDetails();
            UpdateCurrentPageResults("최신 목록 순위 조회 대기");
            RenderGrid();
            _store.SaveSnapshots(_snapshots);
            SaveCurrentListingCache();

            if (_ownListings.Count == 0)
            {
                SetLoadedListingIdentity(_settings.GroupId);
                SetStatus($"매물목록 재조회 완료 · 최신 매물 0건 · 삭제 {reconciliation.RemovedListings.Count}건");
                return;
            }

            SetStatus($"매물목록 재조회 완료 · 최신 {_ownListings.Count}건 · 전체 순위 조회를 시작합니다.");
            var rankingTargets = ListingSorter
                .Sort(_ownListings.ToList(), _rankingCache, _listingSortOrder)
                .ToList();
            var rankingSummary = await RankListingsCoreAsync(
                rankingTargets,
                true,
                $"최신 매물 전체 {_ownListings.Count}건",
                false);

            SetLoadedListingIdentity(_settings.GroupId);
            SaveCurrentListingCache();
            var scope = $"최신 {_ownListings.Count}건 · 신규 {reconciliation.AddedListings.Count}건 · 삭제 {reconciliation.RemovedListings.Count}건";
            SetStatus($"완료: {scope} · 순위 성공 {rankingSummary.SuccessCount}건 / 실패 {rankingSummary.FailureCount}건");
            ShowRankingCompletionPopup(
                scope,
                rankingSummary.SuccessCount,
                rankingSummary.FailureCount,
                rankingSummary.Events);
            EnsureAutoRetryFailedRankingsLoop();
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (NaverApiException ex)
        {
            _store.SaveSettings(_settings);
            SetStatus($"기존 목록은 유지했습니다. 매물목록조회 실패: {ex.Message}");
            if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                HandleRateLimit(_settings.RateLimitBlockedUntilUtc ?? DateTime.UtcNow.AddMinutes(30));
            else
            {
                if (!isAutomatic)
                    MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await RankExistingListingsAfterSynchronizationFailureAsync(ex.Message, isAutomatic);
            }
        }
        catch (Exception ex)
        {
            SaveCurrentListingCache();
            SetStatus($"기존 목록은 유지했습니다. 매물목록조회 실패: {ex.Message}");
            if (!isAutomatic)
                MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await RankExistingListingsAfterSynchronizationFailureAsync(ex.Message, isAutomatic);
        }
        finally
        {
            _refreshing = false;
            EndCancellableOperation();
            SetListingLoadBusy(false);
        }
    }

    private async Task RankExistingListingsAfterSynchronizationFailureAsync(
        string listingError,
        bool isAutomatic)
    {
        if (_ownListings.Count == 0) return;

        var rankingAuthError = NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
        if (rankingAuthError is not null)
        {
            SetStatus($"매물목록 동기화 실패 · 기존 목록 유지 · 랭킹 조회 불가: {rankingAuthError}");
            return;
        }

        try
        {
            var targets = ListingSorter
                .Sort(_ownListings.ToList(), _rankingCache, _listingSortOrder)
                .ToList();
            var scope = $"목록 동기화 실패 후 기존 매물 {_ownListings.Count}건";
            SetStatus($"매물목록 동기화에 실패하여 기존 목록으로 랭킹을 조회합니다: {listingError}");
            var summary = await RankListingsCoreAsync(targets, true, scope, false);
            SetStatus($"완료: 매물목록 동기화 실패 · 기존 목록 유지 · 랭킹 성공 {summary.SuccessCount}건 / 실패 {summary.FailureCount}건");
            ShowRankingCompletionPopup(scope, summary.SuccessCount, summary.FailureCount, summary.Events);
            EnsureAutoRetryFailedRankingsLoop();
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (Exception ex)
        {
            SetStatus($"매물목록 동기화 및 기존 목록 랭킹 조회 실패: {ex.Message}");
            if (!isAutomatic)
                MessageBox.Show(this, ex.Message, "랭킹 조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshRankingOwnListingDetails()
    {
        for (var index = 0; index < _ownListings.Count; index++)
        {
            var listing = _ownListings[index];
            if (!_rankingCache.TryGetValue(listing.ArticleNo, out var result)) continue;
            var merged = MergeListingDetails(listing, result.OwnListing);
            _ownListings[index] = merged;
            _rankingCache[listing.ArticleNo] = result with
            {
                OwnListing = merged
            };
        }
    }

    private RankingResult SynchronizeOwnListingDetails(RankingResult result)
    {
        var ownIndex = _ownListings.FindIndex(listing =>
            string.Equals(listing.ArticleNo, result.OwnListing.ArticleNo, StringComparison.Ordinal));
        if (ownIndex < 0) return result;

        var merged = MergeListingDetails(result.OwnListing, _ownListings[ownIndex]);
        _ownListings[ownIndex] = merged;
        return result with { OwnListing = merged };
    }

    private static Listing MergeListingDetails(Listing preferred, Listing fallback) =>
        new(
            preferred.ArticleNo,
            FirstNotEmpty(preferred.Address, fallback.Address),
            FirstNotEmpty(preferred.TradeType, fallback.TradeType),
            FirstNotEmpty(preferred.Price, fallback.Price),
            FirstNotEmpty(preferred.RealtorName, fallback.RealtorName),
            FirstNotEmpty(preferred.RealtorId, fallback.RealtorId),
            FirstNotEmpty(preferred.ProviderName, fallback.ProviderName),
            FirstNotEmpty(preferred.BuildingName, fallback.BuildingName),
            FirstNotEmpty(preferred.FloorInfo, fallback.FloorInfo),
            FirstNotEmpty(preferred.Area, fallback.Area),
            true)
        {
            ComplexNo = FirstNotEmpty(preferred.ComplexNo, fallback.ComplexNo),
            ArticleName = FirstNotEmpty(preferred.ArticleName, fallback.ArticleName),
            RealEstateType = FirstNotEmpty(preferred.RealEstateType, fallback.RealEstateType),
            Location = FirstNotEmpty(preferred.Location, fallback.Location),
            Description = FirstNotEmpty(preferred.Description, fallback.Description),
            RegisteredDate = FirstNotEmpty(preferred.RegisteredDate, fallback.RegisteredDate),
            VerificationTypeCode = FirstNotEmpty(
                preferred.VerificationTypeCode,
                fallback.VerificationTypeCode),
            SameAddressCount = preferred.SameAddressCount > 0
                ? preferred.SameAddressCount
                : fallback.SameAddressCount
        };

    private sealed record RankingBatchSummary(
        int SuccessCount,
        int FailureCount,
        IReadOnlyList<NotificationEvent> Events)
    {
        public static RankingBatchSummary Empty { get; } = new(0, 0, []);
    }

    private List<Listing> CurrentPageListings()
    {
        // 헤더 정렬이 지정돼 있으면 정렬방법 라디오 대신 그 컬럼 기준으로 정렬한다.
        var sortedListings = _gridSortColumn is null
            ? ListingSorter.Sort(VisibleListings(), _rankingCache, _listingSortOrder)
            : ApplyGridSort(VisibleListings().ToList());
        // 확인 대기 중인 변동(동일생성·금액변동)은 정렬과 무관하게 최상단에 고정한다.
        var pinned = PinnedArticleNumbers();
        if (pinned.Count > 0)
            sortedListings = sortedListings
                .OrderByDescending(listing => pinned.Contains(listing.ArticleNo))
                .ToList();
        return ListingPagination.GetPage(sortedListings, _currentPage, _settings.DisplayPageSize).ToList();
    }

    private IReadOnlyList<Listing> VisibleListings()
    {
        var listings = ListingVisibilityFilter.Apply(_ownListings, _rankingCache, _excludeSingleListings.Checked);
        if (_selectedComplexNames.Count == 0) return listings;
        return listings
            .Where(listing => _selectedComplexNames.Contains(ComplexNameDisplay(listing)))
            .ToList();
    }

    private void UpdateCurrentPageResults(string pendingStatus)
    {
        _results = CurrentPageListings()
            .Select(listing => _rankingCache.TryGetValue(listing.ArticleNo, out var result)
                ? result
                : new RankingResult(listing, null, 0, null, null, [], pendingStatus)
                {
                    PreviousRank = _snapshots.TryGetValue(listing.ArticleNo, out var snapshot)
                        ? snapshot.Rank
                        : null
                })
            .ToList();
    }

    private int PageCount => ListingPagination.GetPageCount(VisibleListings().Count, _settings.DisplayPageSize);

    private void UpdatePagingControls()
    {
        var pageCount = PageCount;
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        var pageSizeText = _settings.DisplayPageSize == 0 ? "전체 표시" : $"페이지당 {_settings.DisplayPageSize}건";
        var visibleCount = VisibleListings().Count;
        var listingCountText = visibleCount == _ownListings.Count
            ? $"전체 {_ownListings.Count}건"
            : $"표시 {visibleCount}건 / 전체 {_ownListings.Count}건";
        _pageLabel.Text = $"{_currentPage} / {pageCount} 페이지  ·  {listingCountText}  ·  {pageSizeText}";
        _previousPageButton.Enabled = !_refreshing && _currentPage > 1;
        _nextPageButton.Enabled = !_refreshing && _currentPage < pageCount;
    }

    private void RenderGrid()
    {
        PopulateComplexNameFilter();
        UpdateSortGlyphs();
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var result in _results)
        {
            AddParentRow(result);
            if (!_expanded.Contains(result.OwnListing.ArticleNo)) continue;
            foreach (var comparable in result.Comparables)
                AddComparableRow(result, comparable);
        }
        _grid.ResumeLayout();
        UpdatePagingControls();
        _excelExportButton.Enabled = !_refreshing && _results.Count > 0;
    }

    private void AddParentRow(RankingResult result)
    {
        var listing = result.OwnListing;
        var expandable = result.Comparables.Count > 0;
        var rowIndex = _grid.Rows.Add(
            result.Comparables.Count > 0 ? "🔍" : string.Empty,
            expandable ? (_expanded.Contains(listing.ArticleNo) ? "▼" : "▶") : string.Empty,
            "내 매물",
            listing.ArticleNo,
            PropertyTypeDisplay(listing),
            ListingNameDisplay(listing),
            listing.TradeType,
            PriceCellDisplay(listing, result),
            RegistrationDateDisplay(listing.RegisteredDate),
            ComplexNameDisplay(listing),
            QueryResultDisplay(result),
            RankPresentation.FormatPrevious(result.PreviousRank),
            RankPresentation.FormatCurrent(result.PreviousRank, result.Rank),
            DuplicateCellDisplay(result),
            listing.RealtorName,
            listing.ProviderName,
            VerificationTypeFormatter.Format(listing.VerificationTypeCode),
            result.Success ? PriceRange(result) : result.Error,
            PropertyAnalysisDisplay(listing),
            listing.Description);
        _grid.Rows[rowIndex].Cells["ComplexNo"].Value = listing.ComplexNo;
        var row = _grid.Rows[rowIndex];
        row.Tag = new GridRowTag(result, listing, false);
        ConfigurePropertyAnalysisCell(row, true);
        row.DefaultCellStyle.Font = GridBoldFont();
        row.DefaultCellStyle.BackColor = Color.FromArgb(244, 250, 247);
        var queryResultCell = row.Cells["QueryResult"];
        var queryResultColor = result.Success
            ? Color.FromArgb(0, 128, 64)
            : Color.Firebrick;
        queryResultCell.Style.ForeColor = queryResultColor;
        queryResultCell.Style.SelectionForeColor = queryResultColor;
        queryResultCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        var currentRankCell = row.Cells["CurrentRank"];
        currentRankCell.Style.BackColor = Color.FromArgb(255, 246, 194);
        currentRankCell.Style.SelectionBackColor = Color.FromArgb(255, 232, 128);
        currentRankCell.Style.ForeColor = Color.FromArgb(104, 82, 12);
        currentRankCell.Style.SelectionForeColor = Color.FromArgb(78, 60, 0);
        var movement = RankPresentation.GetMovement(result.PreviousRank, result.Rank);
        if (movement != RankMovement.None)
        {
            var movementColor = movement == RankMovement.Up ? Color.Red : Color.Blue;
            currentRankCell.Style.ForeColor = movementColor;
            currentRankCell.Style.SelectionForeColor = movementColor;
        }
        if (!result.Success) row.DefaultCellStyle.ForeColor = Color.Firebrick;

        // 확인 대기 중인 변동은 해당 셀을 버튼으로 바꾸고 행 높이를 넉넉히 준다.
        var hasNewDuplicate = _pendingNewDuplicates.Contains(listing.ArticleNo);
        var hasPriceChange = _pendingPriceChanges.ContainsKey(listing.ArticleNo);
        if (hasNewDuplicate)
            ApplyChangeAlertButtonCell(
                row,
                "Total",
                "클릭하면 동일매물 목록을 펼치고 최상단 고정을 해제합니다.");
        if (hasPriceChange)
            ApplyChangeAlertButtonCell(
                row,
                "Price",
                "클릭하면 금액변동 내역을 확인하고 최상단 고정을 해제합니다.");
        if (hasNewDuplicate || hasPriceChange)
        {
            row.Height = 48;
            // 변동 행은 목록에서 바로 눈에 띄도록 배경을 다르게 준다.
            row.DefaultCellStyle.BackColor = Color.FromArgb(255, 240, 240);
        }
    }

    /// <summary>
    /// 단지 한 곳의 광고 상위 중개인(광고 순위)을 조회한다. 단지 광고 API를 쓰며
    /// 단지 상세정보 API와는 별개다. 광고분석 팝업이 열릴 때 단지 수만큼 순서대로 호출한다.
    /// </summary>
    private async Task<IReadOnlyList<ComplexAdvertisementRealtor>> LoadComplexAdvertisementRealtorsAsync(
        AdvertisementComplex complex,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        return await _apiClient.GetComplexAdvertisementRealtorsAsync(
            complex.ComplexNo,
            _settings,
            linked.Token);
    }

    /// <summary>
    /// 단지 한 곳의 상세정보를 단지 정보 API로 조회한다.
    /// 광고분석 팝업에서 행을 선택할 때만 호출되며, 진행 표시 없이 조용히 조회한다.
    /// </summary>
    private async Task<ComplexInformation> LoadComplexInformationAsync(
        AdvertisementComplex complex,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        return await _apiClient.GetComplexInformationAsync(
            complex.ComplexNo,
            complex.ComplexName,
            _settings,
            linked.Token);
    }

    /// <summary>
    /// 팝업 알림이 꺼져 있을 때만 변동을 목록에 남긴다.
    /// 팝업이 켜져 있으면 기존처럼 팝업으로 안내하므로 행을 고정하지 않는다.
    /// </summary>
    private void TrackHighlightedChanges(RankingResult result, ListingSnapshot? previous)
    {
        if (_settings.PopupNotificationsEnabled) return;

        var articleNo = result.OwnListing.ArticleNo;
        if (ListingChangeDetector.IsNewDuplicate(result, previous))
            _pendingNewDuplicates.Add(articleNo);

        var priceChanges = ListingChangeDetector.DetectPriceChanges(result, previous);
        if (priceChanges.Count > 0) _pendingPriceChanges[articleNo] = priceChanges;
    }

    /// <summary>확인 대기 중인 변동이 있어 최상단에 고정할 매물 번호.</summary>
    private HashSet<string> PinnedArticleNumbers()
    {
        var pinned = new HashSet<string>(_pendingNewDuplicates, StringComparer.Ordinal);
        foreach (var articleNo in _pendingPriceChanges.Keys) pinned.Add(articleNo);
        return pinned;
    }

    /// <summary>동일매물 셀 표시값. 단독→동일생성이면 확인 버튼 문구를 아래 줄에 붙인다.</summary>
    private string DuplicateCellDisplay(RankingResult result)
    {
        var text = result.Success ? $"{result.Total}건" : "-";
        return _pendingNewDuplicates.Contains(result.OwnListing.ArticleNo)
            ? $"{text}{Environment.NewLine}동일생성확인"
            : text;
    }

    /// <summary>거래금액 셀 표시값. 금액변동이 있으면 확인 버튼 문구를 아래 줄에 붙인다.</summary>
    private string PriceCellDisplay(Listing listing, RankingResult result)
    {
        _ = result;
        return _pendingPriceChanges.ContainsKey(listing.ArticleNo)
            ? $"{listing.Price}{Environment.NewLine}금액변동확인"
            : listing.Price;
    }

    /// <summary>
    /// 변동이 발생한 셀을 실제 버튼으로 바꾼다. 셀에 표시하던 문구를 그대로 버튼 텍스트로 사용해
    /// 사용자가 눌러야 하는 자리임을 바로 알 수 있게 한다.
    /// </summary>
    private void ApplyChangeAlertButtonCell(DataGridViewRow row, string columnName, string toolTip)
    {
        var columnIndex = _grid.Columns[columnName].Index;
        var text = row.Cells[columnIndex].Value?.ToString() ?? string.Empty;
        var button = new DataGridViewButtonCell
        {
            FlatStyle = FlatStyle.Standard,
            UseColumnTextForButtonValue = false
        };
        row.Cells[columnIndex] = button;
        button.Value = text;
        button.ToolTipText = toolTip;
        button.Style.WrapMode = DataGridViewTriState.True;
        button.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        button.Style.Font = GridBoldFont();
        button.Style.ForeColor = Color.Firebrick;
        button.Style.SelectionForeColor = Color.Firebrick;
        button.Style.Padding = new Padding(2);
    }

    /// <summary>동일생성확인: 접힌 트리를 펼치고 최상단 고정을 해제한다.</summary>
    private void ConfirmNewDuplicate(GridRowTag tag)
    {
        var articleNo = tag.Listing.ArticleNo;
        if (!_pendingNewDuplicates.Remove(articleNo)) return;
        if (tag.Result.Comparables.Count > 0) _expanded.Add(articleNo);
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        SetStatus($"동일생성 확인 · {articleNo} · 동일매물 {tag.Result.Total}건");
    }

    /// <summary>금액변동확인: 변동 내역 팝업을 띄우고 확인하면 최상단 고정을 해제한다.</summary>
    private void ConfirmPriceChange(GridRowTag tag)
    {
        var articleNo = tag.Listing.ArticleNo;
        if (!_pendingPriceChanges.TryGetValue(articleNo, out var changes)) return;

        using (var dialog = new PriceChangeDetailForm(tag.Listing, changes))
            dialog.ShowDialog(this);

        _pendingPriceChanges.Remove(articleNo);
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        SetStatus($"금액변동 확인 · {articleNo} · 변동 {changes.Count}건");
    }

    /// <summary>
    /// 조회결과 컬럼 표시값: 순위조회 성공은 "성공", 조회했지만 실패한 매물은 "실패",
    /// 아직 조회하지 않은(대기 중) 매물은 "-"로 표시한다.
    /// </summary>
    private string QueryResultDisplay(RankingResult result)
    {
        if (result.Success) return "성공";
        return _failedRankingArticleNumbers.Contains(result.OwnListing.ArticleNo) ||
               _rankingCache.ContainsKey(result.OwnListing.ArticleNo)
            ? "실패"
            : "-";
    }

    private void AddComparableRow(RankingResult result, Listing listing)
    {
        var rowIndex = _grid.Rows.Add(
            string.Empty,
            string.Empty,
            listing.IsMine ? "내 매물" : "동일매물",
            $"└ {listing.ArticleNo}",
            PropertyTypeDisplay(listing),
            "    " + ListingNameDisplay(listing),
            listing.TradeType,
            listing.Price,
            RegistrationDateDisplay(listing.RegisteredDate),
            ComplexNameDisplay(listing),
            string.Empty,
            string.Empty,
            result.Comparables.ToList().FindIndex(x => x.ArticleNo == listing.ArticleNo) + 1 + "위",
            string.Empty,
            listing.RealtorName,
            listing.ProviderName,
            VerificationTypeFormatter.Format(listing.VerificationTypeCode),
            JoinDetails(listing),
            "-",
            listing.Description);
        _grid.Rows[rowIndex].Cells["ComplexNo"].Value = listing.ComplexNo;
        var row = _grid.Rows[rowIndex];
        row.Tag = new GridRowTag(result, listing, true);
        ConfigurePropertyAnalysisCell(row, false);
        row.DefaultCellStyle.BackColor = listing.IsMine ? Color.FromArgb(232, 247, 239) : Color.White;
        row.DefaultCellStyle.ForeColor = listing.IsMine ? Color.FromArgb(0, 105, 62) : Color.FromArgb(55, 55, 55);
    }

    private void ExportCurrentListToExcel()
    {
        if (_results.Count == 0)
        {
            MessageBox.Show(this, "Excel로 출력할 매물 목록이 없습니다.", "Excel 출력", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "매물 목록 Excel 저장",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            DefaultExt = "xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"매물목록_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var columns = CurrentExcelColumns();
            var listRows = _results
                .Select(result => BuildExcelRow(result, result.OwnListing, isChild: false, childRank: null))
                .ToList();
            var detailColumns = DetailExcelColumns();
            var detailRows = new List<ExcelExportRow>();
            var detailResults = ExcelDetailResultSelector.Select(_results);
            for (var resultIndex = 0; resultIndex < detailResults.Count; resultIndex++)
            {
                var result = detailResults[resultIndex];
                detailRows.Add(BuildDetailExcelRow(result, result.OwnListing, isChild: false, childRank: null));
                for (var index = 0; index < result.Comparables.Count; index++)
                    detailRows.Add(BuildDetailExcelRow(result, result.Comparables[index], isChild: true, childRank: index + 1));
                if (resultIndex < detailResults.Count - 1)
                    detailRows.Add(new ExcelExportRow(
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        IsSeparator: true));
            }

            ExcelExportService.Export(dialog.FileName, columns, listRows, detailColumns, detailRows);
            SetStatus($"Excel 출력 완료: {dialog.FileName}");
            var openResult = MessageBox.Show(
                this,
                $"Excel 파일을 저장했습니다.\n{dialog.FileName}\n\n지금 파일을 실행하시겠습니까?",
                "Excel 출력 완료",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (openResult == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(dialog.FileName)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    SetStatus($"Excel 저장 완료, 파일 실행 실패: {ex.Message}");
                    MessageBox.Show(
                        this,
                        $"파일은 저장했지만 실행하지 못했습니다.\n{ex.Message}",
                        "Excel 파일 실행 실패",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Excel 출력 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Excel 출력 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private IReadOnlyList<ExcelExportColumn> CurrentExcelColumns() =>
        _grid.Columns.Cast<DataGridViewColumn>()
            .Where(column => column.Name is not "Expand" and not "ComplexNo" and not "Duplicates")
            .OrderBy(column => column.DisplayIndex)
            .Select(column => new ExcelExportColumn(column.Name, column.HeaderText))
            .ToList();

    private static IReadOnlyList<ExcelExportColumn> DetailExcelColumns() =>
    [
        new("CurrentRank", "현재순위"),
        new("Movement", "변동"),
        new("PreviousRank", "이전순위"),
        new("Total", "동일매물"),
        new("ArticleNo", "매물번호"),
        new("BuildingName", "단지/건물명"),
        new("Location", "주소"),
        new("PropertyType", "매물종류"),
        new("Trade", "거래유형"),
        new("Price", "금액"),
        new("RegisteredDate", "등록일"),
        new("Provider", "CP사"),
        new("VerificationMethod", "검증방식"),
        new("Realtor", "중개사무소"),
        new("GroupId", "단체ID")
    ];

    private ExcelExportRow BuildExcelRow(
        RankingResult result,
        Listing listing,
        bool isChild,
        int? childRank)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mine"] = isChild ? listing.IsMine ? "내 매물" : "동일매물" : "내 매물",
            ["ArticleNo"] = isChild ? $"└ {listing.ArticleNo}" : listing.ArticleNo,
            ["PropertyType"] = PropertyTypeDisplay(listing),
            ["Address"] = ListingNameDisplay(listing),
            ["Trade"] = listing.TradeType,
            ["Price"] = listing.Price,
            ["RegisteredDate"] = RegistrationDateDisplay(listing.RegisteredDate),
            ["ComplexName"] = ComplexNameDisplay(listing),
            ["QueryResult"] = isChild ? string.Empty : QueryResultDisplay(result),
            ["PreviousRank"] = isChild ? string.Empty : RankPresentation.FormatPrevious(result.PreviousRank),
            ["CurrentRank"] = isChild
                ? childRank is null ? string.Empty : $"{childRank}위"
                : RankPresentation.FormatCurrent(result.PreviousRank, result.Rank),
            ["Total"] = isChild ? string.Empty : result.Success ? $"{result.Total}건" : "-",
            ["Realtor"] = listing.RealtorName,
            ["Provider"] = listing.ProviderName,
            ["VerificationMethod"] = VerificationTypeFormatter.Format(listing.VerificationTypeCode),
            ["Status"] = isChild
                ? JoinDetails(listing)
                : result.Success ? PriceRange(result) : result.Error ?? string.Empty,
            ["PropertyAnalysis"] = isChild ? "-" : PropertyAnalysisDisplay(listing),
            ["Description"] = listing.Description
        };
        return new ExcelExportRow(
            values,
            OutlineLevel: isChild ? 1 : 0,
            HighlightedColumns: isChild ? null : new HashSet<string>(StringComparer.Ordinal) { "CurrentRank" });
    }

    private ExcelExportRow BuildDetailExcelRow(
        RankingResult result,
        Listing listing,
        bool isChild,
        int? childRank)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CurrentRank"] = isChild ? childRank?.ToString() ?? string.Empty : result.Rank?.ToString() ?? "-",
            ["Movement"] = isChild
                ? listing.IsMine ? "내 매물" : string.Empty
                : RankMovementDisplay(result.PreviousRank, result.Rank),
            ["PreviousRank"] = isChild ? string.Empty : result.PreviousRank?.ToString() ?? "-",
            ["Total"] = isChild ? string.Empty : result.Success ? result.Total.ToString() : "-",
            ["ArticleNo"] = isChild ? $"└ {listing.ArticleNo}" : listing.ArticleNo,
            ["BuildingName"] = BuildingNameDisplay(listing),
            ["Location"] = LocationDisplay(listing),
            ["PropertyType"] = PropertyTypeDisplay(listing),
            ["Trade"] = listing.TradeType,
            ["Price"] = listing.Price,
            ["RegisteredDate"] = RegistrationDateDisplay(listing.RegisteredDate),
            ["Provider"] = listing.ProviderName,
            ["VerificationMethod"] = VerificationTypeFormatter.Format(listing.VerificationTypeCode),
            ["Realtor"] = listing.RealtorName,
            ["GroupId"] = FirstNotEmpty(_loadedListingGroupId, _groupId.Text.Trim())
        };
        return new ExcelExportRow(
            values,
            HighlightMine: isChild && listing.IsMine,
            HighlightGroupHeader: !isChild);
    }

    private static string RankMovementDisplay(int? previousRank, int? currentRank)
    {
        if (previousRank is null || currentRank is null || previousRank == currentRank) return string.Empty;
        var difference = Math.Abs(previousRank.Value - currentRank.Value);
        return RankPresentation.GetMovement(previousRank, currentRank) == RankMovement.Up
            ? $"▲{difference}"
            : $"▼{difference}";
    }

    private static string BuildingNameDisplay(Listing listing)
    {
        var values = new[] { listing.ArticleName, listing.BuildingName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length > 0) return string.Join(" · ", values);
        return string.IsNullOrWhiteSpace(listing.Address) ? "-" : listing.Address;
    }

    private static string LocationDisplay(Listing listing) =>
        FirstNotEmpty(listing.Location, listing.Address, "-");

    private static string RegistrationDateDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy.MM.dd", "yyMMdd", "yy-MM-dd", "yy.MM.dd" };
        return DateTime.TryParseExact(
            value.Trim(),
            formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var date)
            ? date.ToString("yy.MM.dd", System.Globalization.CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private Font GridBoldFont() => _gridBoldFont ??= new Font(_grid.Font, FontStyle.Bold);

    private void ConfigurePropertyAnalysisCell(DataGridViewRow row, bool isOwnListing)
    {
        var columnIndex = _grid.Columns["PropertyAnalysis"].Index;
        if (!isOwnListing)
        {
            row.Cells[columnIndex] = new DataGridViewTextBoxCell
            {
                Value = "-",
                Style = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            };
            return;
        }

        row.Cells[columnIndex].Value = "분석";
        row.Cells[columnIndex].ToolTipText = "이 매물과 동일매물 전체의 상세정보를 비교합니다.";
        row.Cells[columnIndex].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        row.Cells[columnIndex].Style.BackColor = Color.FromArgb(232, 247, 239);
        row.Cells[columnIndex].Style.SelectionBackColor = Color.FromArgb(201, 232, 215);
    }

    private async void GridOnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Rows[e.RowIndex].Tag is not GridRowTag { IsChild: false } tag) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName == "Duplicates")
        {
            OpenDuplicateListingPopup(tag);
            return;
        }
        if (columnName == "PropertyAnalysis")
        {
            await OpenPropertyAnalysisAsync(tag.Listing);
            return;
        }
        if (columnName == "Total" && _pendingNewDuplicates.Contains(tag.Listing.ArticleNo))
        {
            ConfirmNewDuplicate(tag);
            return;
        }
        if (columnName == "Price" && _pendingPriceChanges.ContainsKey(tag.Listing.ArticleNo))
        {
            ConfirmPriceChange(tag);
            return;
        }
        if (columnName != "Expand") return;
        if (tag.Result.Comparables.Count == 0) return;

        if (!_expanded.Add(tag.Listing.ArticleNo)) _expanded.Remove(tag.Listing.ArticleNo);
        RenderGrid();
    }

    private void GridOnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not GridRowTag tag) return;
        if (_grid.Columns[e.ColumnIndex].Name is "Expand" or "PropertyAnalysis" or "Duplicates") return;
        if (string.IsNullOrWhiteSpace(tag.Listing.ArticleNo)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NaverArticleLinkBuilder.Build(tag.Listing),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"브라우저를 열 수 없습니다: {ex.Message}");
        }
    }

    private bool CanUseAdvertisementAnalysis => _authenticationSession?.Grade == 2;

    /// <summary>
    /// 돋보기 버튼: 해당 매물의 동일매물 목록을 일반 창으로 연다.
    /// 모달이 아니라 열어 둔 채로 본 화면을 계속 쓸 수 있고, 창에서 직접 새로고침할 수 있다.
    /// </summary>
    private void OpenDuplicateListingPopup(GridRowTag tag)
    {
        if (tag.Result.Comparables.Count == 0)
        {
            SetStatus($"동일매물이 없습니다 · {tag.Listing.ArticleNo}");
            return;
        }

        var articleNo = tag.Listing.ArticleNo;
        var groupId = FirstNotEmpty(_loadedListingGroupId, _groupId.Text.Trim(), _settings.GroupId);
        ShowDataPopup(
            articleNo,
            () => new DuplicateListingForm(
                tag.Result,
                groupId,
                token => RefreshDuplicateListingAsync(articleNo, token)),
            popup => popup.ShowListing(
                tag.Result,
                token => RefreshDuplicateListingAsync(articleNo, token)));
    }

    /// <summary>동일매물 팝업의 새로고침. 해당 매물만 다시 조회하고 결과를 본 화면에도 반영한다.</summary>
    private async Task<RankingResult> RefreshDuplicateListingAsync(
        string articleNo,
        CancellationToken cancellationToken)
    {
        var listing = _ownListings.FirstOrDefault(item =>
            string.Equals(item.ArticleNo, articleNo, StringComparison.Ordinal));
        if (listing is null)
            return new RankingResult(
                new Listing(articleNo, string.Empty, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, true),
                null, 0, null, null, [], "목록에서 매물을 찾을 수 없습니다.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var ownNumbers = _ownListings.Select(item => item.ArticleNo).ToHashSet(StringComparer.Ordinal);
        var refreshed = await _apiClient.GetRankingAsync(listing, ownNumbers, _settings, linked.Token);
        if (!refreshed.Success) return refreshed;

        var synchronized = SynchronizeOwnListingDetails(refreshed with
        {
            PreviousRank = _rankingCache.TryGetValue(articleNo, out var previous) ? previous.Rank : null
        });
        _rankingCache[articleNo] = synchronized;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        SaveCurrentListingCache();
        return synchronized;
    }

    /// <summary>
    /// 랭킹 결과에서 상위 동일매물 2건의 상세정보를 받아 물건분석 비교 자료를 만든다.
    /// 최초 표시와 팝업 새로고침에서 함께 사용한다.
    /// </summary>
    private async Task<AdvertisementListingAnalysis> BuildPropertyAnalysisAsync(
        RankingResult rankingResult,
        CancellationToken cancellationToken)
    {
        var competitors = rankingResult.Comparables
            .Select((item, index) => new { Listing = item, ExposureRank = index + 1 })
            .Where(item => !item.Listing.IsMine &&
                           !string.Equals(
                               item.Listing.ArticleNo,
                               rankingResult.OwnListing.ArticleNo,
                               StringComparison.Ordinal))
            .GroupBy(item => item.Listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.ExposureRank)
            .Take(2)
            .ToList();

        var detailCache = new Dictionary<string, ArticleComparisonDetail>(StringComparer.Ordinal);
        var detailRequestTotal = competitors.Count + 1;
        var detailRequestIndex = 0;
        async Task<ArticleComparisonDetail> LoadDetailAsync(Listing target)
        {
            if (detailCache.TryGetValue(target.ArticleNo, out var cached)) return cached;
            detailRequestIndex++;
            SetStatus($"물건분석 상세정보 조회 중 · {detailRequestIndex}/{detailRequestTotal} · {target.ArticleNo}");
            try
            {
                var detail = await _apiClient.GetArticleComparisonDetailAsync(
                    target,
                    _settings,
                    cancellationToken);
                detailCache[target.ArticleNo] = detail;
                return detail;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = new ArticleComparisonDetail(target) { Error = ex.Message };
                detailCache[target.ArticleNo] = failed;
                return failed;
            }
        }

        var ownDetail = await LoadDetailAsync(rankingResult.OwnListing);
        var synchronizedResult = SynchronizeOwnListingDetails(rankingResult with
        {
            OwnListing = MergeListingDetails(ownDetail.Listing, rankingResult.OwnListing)
        });
        _rankingCache[synchronizedResult.OwnListing.ArticleNo] = synchronizedResult;

        var comparisonDetails = new List<RankedArticleComparison>(competitors.Count);
        for (var index = 0; index < competitors.Count; index++)
        {
            var competitor = competitors[index];
            comparisonDetails.Add(new RankedArticleComparison(
                index + 1,
                competitor.ExposureRank,
                await LoadDetailAsync(competitor.Listing)));
        }

        return new AdvertisementListingAnalysis(
            synchronizedResult,
            ownDetail with { Listing = synchronizedResult.OwnListing },
            comparisonDetails);
    }

    /// <summary>물건분석 팝업의 새로고침. 동일매물을 다시 조회하고 상세정보까지 새로 받는다.</summary>
    private async Task<AdvertisementListingAnalysis> RefreshPropertyAnalysisAsync(
        string articleNo,
        CancellationToken cancellationToken)
    {
        var refreshed = await RefreshDuplicateListingAsync(articleNo, cancellationToken);
        if (!refreshed.Success) throw new InvalidOperationException(refreshed.Error ?? "동일매물 조회 실패");
        return await BuildPropertyAnalysisAsync(refreshed, cancellationToken);
    }

    /// <summary>
    /// 같은 매물의 같은 팝업이 열려 있으면 새 창을 만들지 않고 그 창을 갱신한 뒤 앞으로 가져온다.
    /// 다른 매물이면 별도 창으로 계속 생성해 여러 매물을 나란히 비교할 수 있다.
    /// 광고분석처럼 매물 단위가 아닌 팝업은 고정 키를 써서 항상 한 창만 유지한다.
    /// </summary>
    private TPopup ShowDataPopup<TPopup>(
        string articleNo,
        Func<TPopup> create,
        Action<TPopup>? update = null)
        where TPopup : Form
    {
        var key = $"{typeof(TPopup).Name}:{articleNo}";
        if (_dataPopups.TryGetValue(key, out var tracked) &&
            tracked is TPopup existing &&
            !existing.IsDisposed)
        {
            update?.Invoke(existing);
            FocusDataPopup(existing);
            return existing;
        }

        var popup = create();
        _dataPopups[key] = popup;
        popup.FormClosed += (_, _) =>
        {
            // 모달이 아닌 창은 Close()가 이미 Dispose까지 하므로 여기서 다시 Dispose하지 않는다.
            if (_dataPopups.TryGetValue(key, out var current) && ReferenceEquals(current, popup))
                _dataPopups.Remove(key);
        };
        // 소유 창을 지정하면 팝업이 항상 본 창 위에 떠서 본 창을 선택해도 앞으로 나오지 않는다.
        // 독립 창으로 띄워 일반 창처럼 앞뒤를 오갈 수 있게 한다.
        popup.StartPosition = FormStartPosition.Manual;
        popup.Location = NextPopupLocation(popup.Size);
        popup.Show();
        FocusDataPopup(popup);
        return popup;
    }

    /// <summary>
    /// 새 팝업 위치. 본 창 근처에서 열린 팝업 수만큼 어긋나게 놓아 창이 겹쳐 가려지지 않게 한다.
    /// 화면 밖으로 나가면 처음 위치로 되돌린다.
    /// </summary>
    private Point NextPopupLocation(Size popupSize)
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        var origin = new Point(Left + 40, Top + 40);
        var offset = Math.Min(_dataPopups.Count, 6) * 28;
        var location = new Point(origin.X + offset, origin.Y + offset);
        if (location.X + popupSize.Width > workingArea.Right ||
            location.Y + popupSize.Height > workingArea.Bottom)
            location = origin;
        return new Point(
            Math.Max(workingArea.Left, Math.Min(location.X, workingArea.Right - popupSize.Width)),
            Math.Max(workingArea.Top, Math.Min(location.Y, workingArea.Bottom - popupSize.Height)));
    }

    /// <summary>
    /// 팝업 창을 앞으로 가져온다.
    /// 버튼 클릭 처리가 끝나면 본 창이 다시 활성화되면서 팝업이 뒤로 밀리므로,
    /// 현재 메시지 처리가 끝난 뒤에 활성화하도록 미룬다.
    /// </summary>
    private void FocusDataPopup(Form popup)
    {
        BeginInvoke(() =>
        {
            if (popup.IsDisposed) return;
            if (!popup.Visible) popup.Show();
            if (popup.WindowState == FormWindowState.Minimized)
                popup.WindowState = FormWindowState.Normal;
            popup.BringToFront();
            popup.Activate();
            popup.Focus();
        });
    }

    /// <summary>열려 있는 데이터 팝업을 모두 닫는다. 종료·로그아웃 때 호출한다.</summary>
    private void CloseDataPopups()
    {
        foreach (var popup in _dataPopups.Values.ToList())
        {
            if (popup.IsDisposed) continue;
            popup.Close();
        }
        _dataPopups.Clear();
    }

    /// <summary>
    /// 목록·랭킹이 갱신되면 열려 있는 팝업에 알린다.
    /// 방금 받은 랭킹 결과를 그대로 넘기므로 팝업이 API를 다시 부르지 않는다.
    /// API 재조회는 사용자가 팝업의 새로고침을 눌렀을 때만 일어난다.
    /// </summary>
    private void UpdateDataPopupsFromListings()
    {
        var popups = _dataPopups.Values
            .OfType<IReloadablePopup>()
            .Where(popup => popup is Form { IsDisposed: false })
            .ToList();
        foreach (var popup in popups)
        {
            try
            {
                popup.OnListingsUpdated(_rankingCache);
            }
            catch
            {
                // 팝업 갱신 실패는 본 화면 흐름을 막지 않는다.
            }
        }
    }

    private async Task OpenPropertyAnalysisAsync(Listing listing)
    {
        if (_refreshing)
        {
            SetStatus("다른 조회가 진행 중입니다. 완료 후 분석을 다시 눌러 주세요.");
            return;
        }
        //if (!CanUseAdvertisementAnalysis)
        //{
        //    MessageBox.Show(
        //        this,
        //        "물건분석은 회원 등급 2 계정에서 사용할 수 있습니다.",
        //        "물건분석",
        //        MessageBoxButtons.OK,
        //        MessageBoxIcon.Information);
        //    return;
        //}

        var authError = NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
        if (authError is not null)
        {
            MessageBox.Show(this, authError, "인증 설정 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } propertyAnalysisBlockedUntil &&
            propertyAnalysisBlockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(propertyAnalysisBlockedUntil);
            return;
        }

        _refreshing = true;
        _propertyAnalysisInProgress.Add(listing.ArticleNo);
        SetBusy(true);
        RenderGrid();
        try
        {
            SetStatus($"물건분석 동일매물 조회 중 · {listing.ArticleNo}");
            await RankListingsCoreAsync(
                [listing],
                true,
                $"물건분석 {listing.ArticleNo}",
                false);

            if (!_rankingCache.TryGetValue(listing.ArticleNo, out var rankingResult) || !rankingResult.Success)
            {
                MessageBox.Show(
                    this,
                    "동일매물 정보를 조회하지 못했습니다. 실패 재조회 후 다시 시도해 주세요.",
                    "물건분석",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var competitors = rankingResult.Comparables
                .Select((item, index) => new { Listing = item, ExposureRank = index + 1 })
                .Where(item => !item.Listing.IsMine &&
                               !string.Equals(
                                   item.Listing.ArticleNo,
                                   rankingResult.OwnListing.ArticleNo,
                                   StringComparison.Ordinal))
                .GroupBy(item => item.Listing.ArticleNo, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.ExposureRank)
                .Take(2)
                .ToList();
            if (competitors.Count == 0)
            {
                MessageBox.Show(
                    this,
                    $"해당 매물에 동일매물이 없습니다.\n매물번호: {listing.ArticleNo}",
                    "물건분석",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                SetStatus($"물건분석 종료 · 동일매물 없음 · {listing.ArticleNo}");
                return;
            }

            var analysis = await BuildPropertyAnalysisAsync(rankingResult, _lifetime.Token);
            SaveCurrentListingCache();
            UpdateCurrentPageResults("랭킹 미조회");
            RenderGrid();
            SetStatus($"물건분석 표시 · {listing.ArticleNo} · 상위 동일매물 {analysis.TopAdvertisements.Count}건");
            var articleNo = listing.ArticleNo;
            ShowDataPopup(
                articleNo,
                () => new PropertyAnalysisForm(
                    analysis,
                    token => RefreshPropertyAnalysisAsync(articleNo, token)),
                popup => popup.ShowAnalysis(
                    analysis,
                    token => RefreshPropertyAnalysisAsync(articleNo, token)));
        }
        catch (OperationCanceledException)
        {
            // 앱 종료 또는 사용자의 조회중단.
            HandleOperationCancelled();
        }
        catch (Exception ex)
        {
            if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
                HandleRateLimit(blockedUntil);
            SetStatus($"물건분석 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "물건분석 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _propertyAnalysisInProgress.Remove(listing.ArticleNo);
            _refreshing = false;
            SetBusy(false);
            RenderGrid();
        }
    }

    /// <summary>
    /// 광고분석 버튼: 이미 바인딩된 단지번호만 중복 없이 추려 팝업을 바로 연다.
    /// 단지별 광고 순위와 단지 상세정보 조회는 팝업이 직접 수행한다.
    /// </summary>
    private void ShowOwnedComplexListPopup()
    {
        if (_refreshing) return;
        if (!CanUseAdvertisementAnalysis)
        {
            MessageBox.Show(
                this,
                "광고분석은 회원 등급 2 계정에서 사용할 수 있습니다.",
                "광고분석",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (_ownListings.Count == 0)
        {
            MessageBox.Show(
                this,
                "매물목록조회를 먼저 실행해 주세요.",
                "광고분석",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        _refreshing = true;
        SetBusy(true);
        try
        {
            // 단지번호는 매물동기화·순위조회 때 매물 1건씩 이미 바인딩해 두었으므로
            // 여기서는 재조회하지 않고 목록에 있는 단지번호만 중복 제거해 바로 팝업을 연다.
            var complexes = AdvertisementAnalysisService.GroupOwnedComplexes(_ownListings);
            if (complexes.Count == 0)
            {
                // 단지번호가 있는 매물이 한 건이라도 있으면 그 단지들로 정상 진행한다.
                // 여기로 들어오는 경우는 조회된 매물 전부에 단지번호가 없을 때뿐이다.
                MessageBox.Show(
                    this,
                    $"조회된 매물 {_ownListings.Count}건 모두 단지번호가 없어 광고분석을 할 수 없습니다.\n\n" +
                    "상가·사무실·토지처럼 단지가 없는 매물이거나,\n" +
                    "순위조회를 아직 하지 않아 단지번호가 채워지지 않은 경우입니다.",
                    "광고분석",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // 광고 순위는 팝업이 열린 뒤 팝업 중앙 진행 표시와 함께 단지 수만큼 조회하고,
            // 단지 상세정보는 목록에서 행을 선택할 때 그 단지만 조회한다.
            SetStatus($"광고분석 · 단지 {complexes.Count}곳");
            var advertisementGroupId =
                FirstNotEmpty(_loadedListingGroupId, _groupId.Text.Trim(), _settings.GroupId);
            ShowDataPopup(
                "all",
                () => new OwnedComplexListForm(
                    complexes,
                    advertisementGroupId,
                    LoadComplexAdvertisementRealtorsAsync,
                    LoadComplexInformationAsync),
                popup => popup.ShowComplexes(complexes, advertisementGroupId));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _refreshing = false;
            SetBusy(false);
            RenderGrid();
        }
    }

    /// <summary>
    /// 로그아웃. 프로그램을 끌지 로그인 화면으로 돌아갈지 고르게 한다.
    /// 어느 쪽이든 서버 세션은 닫히므로 PC 자리가 바로 반환된다.
    /// 조회가 진행 중이면 먼저 중단하도록 안내한다.
    /// </summary>
    private void Logout()
    {
        if (_refreshing)
        {
            MessageBox.Show(
                this,
                "조회가 진행 중입니다. 조회중단 후 로그아웃해 주세요.",
                "로그아웃",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "시스템을 종료하시겠습니까?\n\n" +
            "예 · 로그아웃하고 프로그램을 종료합니다.\n" +
            "아니요 · 로그아웃하고 로그인 화면으로 이동합니다.\n" +
            "취소 · 로그아웃하지 않고 계속 사용합니다.",
            "로그아웃",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button3);
        if (answer is not (DialogResult.Yes or DialogResult.No)) return;

        // 접속 확인을 멈춰 종료 중에 세션 종료 팝업이 겹치지 않게 한다.
        _sessionTerminated = true;
        StopSessionHeartbeat();
        ReturnToLogin = answer == DialogResult.No;
        ExitApplication();
    }

    private void OpenSettings()
    {
        ShowSettingsDialog();
    }

    private bool ShowSettingsDialog()
    {
        var popupNotificationsWereEnabled = _settings.PopupNotificationsEnabled;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;
        _settings.StartMinimized = false;
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        _settings = dialog.EditedSettings;
        _settings.ManualArticleNumbers = string.Empty;
        _settings.DisplayPageSize = 0;
        _settings.RankImmediatelyAfterListingLoad = true;
        _settings.StartMinimized = false;
        _store.SaveSettings(_settings);
        ConfigureTimer();
        UpdateCooldownUi();
        RenderGrid();
        if (popupNotificationsWereEnabled && !_settings.PopupNotificationsEnabled)
            CloseNotificationPopups();
        SetStatus("설정을 저장했습니다.");
        return true;
    }

    private void ConfigureTimer()
    {
        _timer.Stop();
        _settings.PollIntervalMinutes = AppSettings.NormalizePollInterval(_settings.PollIntervalMinutes);
        _timer.Interval = checked(_settings.PollIntervalMinutes * 60 * 1000);
        if (_settings.AutoRefresh)
        {
            _timer.Start();
            _nextScheduledRefreshUtc = DateTime.UtcNow.AddMilliseconds(_timer.Interval);
        }
        else
        {
            _nextScheduledRefreshUtc = null;
        }
    }

    private void ApplySettingsToUi()
    {
        _groupId.Text = _settings.GroupId;
        _saveGroupId.Checked = _settings.SaveGroupId;
        _articleNumbers.Text = string.Empty;
        _rankImmediately.Checked = true;
    }

    private void RestoreListingCache()
    {
        var loginId = CurrentLoginId();
        var groupId = _groupId.Text.Trim();
        var cache = _store.LoadListingCache(loginId, groupId);
        if (cache is null) return;

        _ownListings = cache.Listings
            .GroupBy(listing => listing.ArticleNo, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        _rankingCache.Clear();
        var ownArticleNumbers = _ownListings
            .Select(listing => listing.ArticleNo)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var result in cache.RankingResults)
        {
            if (!ownArticleNumbers.Contains(result.OwnListing.ArticleNo)) continue;
            _rankingCache[result.OwnListing.ArticleNo] = result;
        }
        _failedRankingArticleNumbers.Clear();
        foreach (var result in _rankingCache.Values.Where(result => !result.Success))
            _failedRankingArticleNumbers.Add(result.OwnListing.ArticleNo);

        _expanded.Clear();
        _currentPage = 1;
        _hasLoadedListings = _ownListings.Count > 0;
        _loadedListingLoginId = cache.LoginId;
        _loadedListingGroupId = cache.GroupId;
        _restoredListingCacheAtUtc = cache.SavedAtUtc;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        UpdateRetryFailedRankingsButton();
        _lastChecked.Text = $"최종조회일시: {cache.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private void SetLoadedListingIdentity(string groupId)
    {
        var loginId = CurrentLoginId();
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(groupId)) return;
        _loadedListingLoginId = loginId.Trim();
        _loadedListingGroupId = groupId.Trim();
        _restoredListingCacheAtUtc = null;
    }

    private void SaveCurrentListingCache()
    {
        if (string.IsNullOrWhiteSpace(_loadedListingLoginId) ||
            string.IsNullOrWhiteSpace(_loadedListingGroupId)) return;

        var rankingResults = _ownListings
            .Where(listing => _rankingCache.ContainsKey(listing.ArticleNo))
            .Select(listing => _rankingCache[listing.ArticleNo])
            .ToList();
        _store.SaveListingCache(new ListingCacheEntry(
            _loadedListingLoginId,
            _loadedListingGroupId,
            DateTime.UtcNow,
            _ownListings.ToList(),
            rankingResults));
    }

    private string CurrentLoginId() =>
        _authenticationSession?.UserId?.Trim() ?? _settings.LastLoginId.Trim();

    private void HandleRateLimit(DateTime blockedUntilUtc)
    {
        SetStatus(FormatRateLimitStatus(blockedUntilUtc));
        UpdateCooldownUi();
        if (_lastRateLimitNoticeUntilUtc == blockedUntilUtc) return;
        _lastRateLimitNoticeUntilUtc = blockedUntilUtc;
        _trayIcon.BalloonTipTitle = "네이버 호출 제한";
        _trayIcon.BalloonTipText = $"{blockedUntilUtc.ToLocalTime():HH:mm}까지 조회를 자동 중지했습니다. 앱을 재시작할 필요가 없습니다.";
        _trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _trayIcon.ShowBalloonTip(6000);
    }

    private string FormatRateLimitStatus(DateTime blockedUntilUtc)
    {
        var source = string.IsNullOrWhiteSpace(_settings.RateLimitCooldownSource)
            ? "이전 버전에서 저장된 보호 대기(기본 최대 30분)"
            : _settings.RateLimitCooldownSource;
        return $"네이버 429 응답 · {source} · {blockedUntilUtc.ToLocalTime():HH:mm}까지 요청 중지";
    }

    private void PopulateNotices()
    {
        _notices = _settings.Notices
            .Where(notice => !string.IsNullOrWhiteSpace(notice))
            .Select(notice => notice.Trim())
            .ToArray();
        if (_notices.Length == 0) _notices = ["등록된 공지사항이 없습니다."];
        _noticeScroll.Minimum = 0;
        _noticeScroll.LargeChange = 1;
        _noticeScroll.SmallChange = 1;
        _noticeScroll.Maximum = Math.Max(0, _notices.Length - 1);
        _noticeScroll.Enabled = _notices.Length > 1;
        _noticeScroll.Value = 0;
        ShowNotice(0);
        _noticeRotationTimer.Stop();
        if (_notices.Length > 1) _noticeRotationTimer.Start();
    }

    private void NoticeOnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_notices.Length <= 1) return;
        var offset = e.Delta < 0 ? 1 : -1;
        _noticeScroll.Value = Math.Clamp(_noticeScroll.Value + offset, 0, _notices.Length - 1);
        ShowNotice(_noticeScroll.Value);
    }

    private void ShowNotice(int index)
    {
        if (_notices.Length == 0) return;
        var safeIndex = Math.Clamp(index, 0, _notices.Length - 1);
        _noticeText.Text = $"{safeIndex + 1}/{_notices.Length}  {_notices[safeIndex]}";
        _noticeText.AccessibleDescription = _notices[safeIndex];
    }

    private void AdvanceNotice()
    {
        if (_notices.Length <= 1) return;
        var nextIndex = (_noticeScroll.Value + 1) % _notices.Length;
        _noticeScroll.Value = nextIndex;
        ShowNotice(nextIndex);
    }

    private static string BuildWindowTitle(AuthenticationSession? session)
    {
        const string applicationTitle = "매물분석알림";
        if (session is null) return applicationTitle;
        var remainingDays = session.MembershipEnd is { } membershipEnd
            ? Math.Max(0, (membershipEnd.Date - DateTime.Today).Days)
            : 0;
        return $"{applicationTitle} - {session.Name} ({session.UserId}) - 구독 {remainingDays}일 남음";
    }

    private void NormalizeLoadedRankingOwnership(ISet<string> ownArticleNumbers)
    {
        foreach (var articleNo in _rankingCache.Keys.ToList())
        {
            var result = _rankingCache[articleNo];
            _rankingCache[articleNo] = result with
            {
                OwnListing = result.OwnListing with { IsMine = true },
                Comparables = result.Comparables
                    .Select(listing => listing with { IsMine = ownArticleNumbers.Contains(listing.ArticleNo) })
                    .ToList()
            };
        }
    }

    private string? GetRequiredAuthError()
    {
        var hasDirectArticleNumbers = NaverLandClient.ParseManualArticleNumbers(_settings.ManualArticleNumbers).Count > 0;
        if (!hasDirectArticleNumbers)
        {
            var realtorError = NaverAuthValidator.GetProfileError(
                "중개인 매물 목록 API", _apiConfiguration.RealtorArticleList);
            if (realtorError is not null) return realtorError;
        }
        return NaverAuthValidator.GetProfileError("랭킹 API", _apiConfiguration.Ranking);
    }

    private void UpdateCooldownUi()
    {
        var blocked = _settings.RateLimitBlockedUntilUtc is { } until && until > DateTime.UtcNow;
        _loadButton.Enabled = !_refreshing && !blocked;
        _refreshButton.Enabled = !_refreshing && !blocked && _hasLoadedListings;
        UpdateRetryFailedRankingsButton(_refreshing);
        UpdatePagingControls();
        if (blocked)
        {
            _cooldownUiTimer.Start();
            SetStatus(FormatRateLimitStatus(_settings.RateLimitBlockedUntilUtc!.Value));
        }
        else
        {
            _cooldownUiTimer.Stop();
            if (_settings.RateLimitBlockedUntilUtc is not null)
            {
                _settings.RateLimitBlockedUntilUtc = null;
                _settings.RateLimitCooldownSource = string.Empty;
                _store.SaveSettings(_settings);
                SetStatus("429 보호 대기가 끝났습니다. 다시 조회할 수 있습니다.");
            }
        }
    }

    private void UpdateRetryFailedRankingsButton(bool busy = false)
    {
        var ownArticleNumbers = _ownListings
            .Select(listing => listing.ArticleNo)
            .ToHashSet(StringComparer.Ordinal);
        _failedRankingArticleNumbers.RemoveWhere(articleNo => !ownArticleNumbers.Contains(articleNo));
        var failureCount = _failedRankingArticleNumbers.Count;
        var blocked = _settings.RateLimitBlockedUntilUtc is { } until && until > DateTime.UtcNow;
        _retryFailedRankingsButton.Text = failureCount == 0
            ? "실패 재조회"
            : $"실패 재조회 ({failureCount})";
        _retryFailedRankingsButton.Enabled = !busy && !blocked && failureCount > 0;
    }

    private void ShowRankingCompletionPopup(
        string scope,
        int successCount,
        int failureCount,
        IReadOnlyList<NotificationEvent> events)
    {
        if (!_settings.PopupNotificationsEnabled || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (!_settings.PopupNotificationsEnabled) return;
            // 조회 완료 팝업을 표시하기 전에 남아 있는 진행 차광창을 항상 해제한다.
            _busyOverlay.Hide();
            var popupDefinitions = NotificationPopupPlanner.Create(events);
            var titlesToReplace = NotificationPopupPlanner.SelectTitlesToReplace(
                _notificationPopups
                    .Where(popup => !popup.IsDisposed)
                    .Select(popup => popup.Text),
                popupDefinitions);

            // 같은 변동 유형은 이전 창을 닫고 최신 내용으로 교체한다.
            // 이번 조회에서 새 내용이 없는 다른 유형의 창은 사용자가 닫을 때까지 유지한다.
            foreach (var existingPopup in _notificationPopups
                         .Where(popup => !popup.IsDisposed && titlesToReplace.Contains(popup.Text))
                         .ToList())
                existingPopup.Close();

            foreach (var definition in popupDefinitions)
            {
                var cascadeIndex = _notificationPopups.Count(popup => !popup.IsDisposed && popup.Visible);
                var popup = new RankingNotificationForm(
                    _applicationIcon,
                    definition.WindowTitle,
                    definition.Headline,
                    scope,
                    successCount,
                    failureCount,
                    definition.Events,
                    ShowWindow,
                    cascadeIndex);
                _notificationPopups.Add(popup);
                popup.FormClosed += (_, _) =>
                {
                    _notificationPopups.Remove(popup);
                    ActivateAfterNotificationPopupClosed();
                };
                popup.Show();
                popup.BringToFront();
                popup.Activate();
            }
        });
    }

    private void CloseNotificationPopups()
    {
        foreach (var popup in _notificationPopups.Where(popup => !popup.IsDisposed).ToList())
            popup.Close();
    }

    private void ActivateAfterNotificationPopupClosed()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _busyOverlay.Hide();
            var remainingPopup = _notificationPopups.LastOrDefault(popup => !popup.IsDisposed && popup.Visible);
            if (remainingPopup is not null)
            {
                remainingPopup.BringToFront();
                remainingPopup.Activate();
                return;
            }

            if (!Visible) return;
            Enabled = true;
            BringToFront();
            Activate();
        });
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 드래그 도중 발생한 마지막 DisplayIndex까지 다음 실행에 복원되도록 종료 시 재저장한다.
        SaveGridColumnOrder();
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            _reallyExit = true;
            return;
        }
        if (_reallyExit) return;
        if (_refreshing)
        {
            e.Cancel = true;
            SetStatus("현재 조회는 시스템 트레이 백그라운드에서 계속됩니다.");
            _busyOverlay.Hide();
            Hide();
            ShowInTaskbar = false;
            ShowBackgroundTip("현재 랭킹 조회를 백그라운드에서 계속합니다.");
            return;
        }
        e.Cancel = true;
        _settings.RankImmediatelyAfterListingLoad = true;
        _settings.DisplayPageSize = 0;
        _store.SaveSettings(_settings);
        Hide();
        ShowInTaskbar = false;
        ShowBackgroundTip("랭킹 모니터가 시스템 트레이에서 계속 실행됩니다.");
    }

    private void ShowBackgroundTip(string message)
    {
        if (_hideTipShown) return;
        _hideTipShown = true;
        _trayIcon.BalloonTipTitle = "백그라운드에서 실행 중";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(4000);
    }

    private void ShowWindow()
    {
        _busyOverlay.Hide();
        Enabled = true;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        _sessionTerminated = true;
        CloseDataPopups();
        _timer.Stop();
        StopSessionHeartbeat();
        _lifetime.Cancel();
        _trayIcon.Visible = false;
        Close();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ranking-icon.ico");
            if (File.Exists(assetPath)) return new Icon(assetPath);

            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // Use the Windows fallback icon when the packaged asset cannot be read.
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var popup in _notificationPopups.ToList()) popup.Dispose();
            _notificationPopups.Clear();
            // 소유 창이 없는 데이터 팝업은 본 창이 닫혀도 남으므로 여기서 함께 정리한다.
            CloseDataPopups();
            _sessionHeartbeatTimer?.Dispose();
            _timer.Dispose();
            _cooldownUiTimer.Dispose();
            _noticeRotationTimer.Dispose();
            _trayIcon.Dispose();
            _busyOverlay.Dispose();
            _applicationIcon.Dispose();
            _gridBoldFont?.Dispose();
            // 드롭다운을 만든 적이 있으면 호스팅된 목록까지 함께 정리된다.
            if (_complexNameDropDown is not null) _complexNameDropDown.Dispose();
            else _complexNameList.Dispose();
            _operationCancellation?.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 진행 중인 조회에 사용할 취소 토큰. 작업이 없으면 앱 종료 토큰을 사용한다.
    /// </summary>
    private CancellationToken OperationToken => _operationCancellation?.Token ?? _lifetime.Token;

    /// <summary>조회 작업을 시작하며 중단 가능한 토큰을 준비한다.</summary>
    private void BeginCancellableOperation(CancellableOperationKind kind)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancelledByUser = false;
        _operationKind = kind;
    }

    /// <summary>조회 작업을 끝내고 취소 토큰을 정리한다.</summary>
    private void EndCancellableOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _operationKind = CancellableOperationKind.None;
    }

    /// <summary>조회중단 버튼 처리. 진행 중인 요청을 즉시 취소한다.</summary>
    private void CancelCurrentOperation()
    {
        var cancellation = _operationCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        _operationCancelledByUser = true;
        SetStatus("조회를 중단하는 중입니다...");
        cancellation.Cancel();
    }

    /// <summary>취소 예외 처리. 앱 종료 중이면 조용히 넘어간다.</summary>
    private void HandleOperationCancelled()
    {
        if (_lifetime.IsCancellationRequested) return;
        SetStatus("조회를 중단했습니다.");
    }

    /// <summary>
    /// 진행 중인 작업을 시작한 버튼만 조회중단으로 바꿔 누를 수 있게 한다.
    /// 나머지 버튼은 기존처럼 조회가 끝날 때까지 비활성 상태로 둔다.
    /// 자동 조회로 시작한 작업도 성격에 맞는 버튼에서 중단할 수 있다.
    /// </summary>
    private void ApplyOperationButtonState(bool busy, bool blocked)
    {
        var cancellable = busy && _operationCancellation is not null;
        var loadCancels = cancellable && _operationKind == CancellableOperationKind.ListingSync;
        var refreshCancels = cancellable && _operationKind == CancellableOperationKind.RankingRefresh;
        _loadButton.Text = loadCancels ? CancelOperationButtonText : LoadButtonDefaultText;
        _refreshButton.Text = refreshCancels ? CancelOperationButtonText : RefreshButtonDefaultText;
        _loadButton.Enabled = loadCancels || (!busy && !blocked);
        _refreshButton.Enabled = refreshCancels || (!busy && !blocked && _hasLoadedListings);
    }

    private void SetBusy(bool busy)
    {
        var blocked = _settings.RateLimitBlockedUntilUtc is { } until && until > DateTime.UtcNow;
        ApplyOperationButtonState(busy, blocked);
        UpdateRetryFailedRankingsButton(busy);
        _advertisementAnalysisButton.Enabled = !busy && CanUseAdvertisementAnalysis;
        _settingsButton.Enabled = !busy;
        _logoutButton.Enabled = !busy;
        _excelExportButton.Enabled = !busy && _results.Count > 0;
        _groupId.Enabled = !busy;
        _articleNumbers.Enabled = !busy;
        _saveGroupId.Enabled = !busy;
        _pageSizeCombo.Enabled = !busy;
        _rankImmediately.Enabled = !busy;
        // 재조회 중에도 목록 확인, 선택, 스크롤, 정렬 및 창 닫기를 허용한다.
        _grid.Enabled = true;
        ControlBox = true;
        UpdatePagingControls();
        UseWaitCursor = false;
        Cursor = Cursors.Default;
        _grid.Cursor = Cursors.Default;
        _busyOverlay.Hide();
        if (!busy) ActiveControl = null;
    }

    private void SetListingLoadBusy(bool busy)
    {
        var blocked = _settings.RateLimitBlockedUntilUtc is { } until && until > DateTime.UtcNow;
        ApplyOperationButtonState(busy, blocked);
        UpdateRetryFailedRankingsButton(busy);
        _advertisementAnalysisButton.Enabled = !busy && CanUseAdvertisementAnalysis;
        _settingsButton.Enabled = !busy;
        _logoutButton.Enabled = !busy;
        _excelExportButton.Enabled = !busy && _results.Count > 0;
        _groupId.Enabled = !busy;
        _saveGroupId.Enabled = !busy;
        _grid.Enabled = true;
        ControlBox = true;
        UseWaitCursor = false;
        Cursor = Cursors.Default;
        _grid.Cursor = Cursors.Default;
        _busyOverlay.Hide();
        UpdatePagingControls();
        if (!busy) ActiveControl = null;
    }

    // 중앙 진행 모달은 사용하지 않는다. 조회 진행은 하단 상태표시줄로만 안내한다.
    private static void ConfigureBusyProgress(int total)
    {
    }

    private static void ReportBusyProgress(int completed, int total, string message)
    {
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        // 하단 상태표시줄은 사용하지 않고 검색조건 줄에만 볼드로 표시한다.
        _progressStatus.Text = text;
    }

    /// <summary>
    /// 조회 진행 표시 전용. 하단 상태표시줄은 건드리지 않고 상단 볼드 텍스트만 갱신한다.
    /// 대량 조회 중 UI 갱신 부담을 줄이기 위해 호출 측에서 25건 단위로만 호출한다.
    /// </summary>
    private void SetProgressStatus(string text)
    {
        _progressStatus.Text = text;
    }

    private async Task<bool> EnsureMemberGroupAsync(string groupId)
    {
        if (_authenticationClient is null || _authenticationSession is null) return true;

        var result = await _authenticationClient.SaveMemberGroupAsync(
            _authenticationSession,
            groupId,
            _lifetime.Token);
        if (result.Success) return true;

        var message = $"회원_단체 확인 또는 저장에 실패했습니다.\n{result.Message}";
        SetStatus(message.Replace('\n', ' '));
        MessageBox.Show(this, message, "회원_단체 확인 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>
    /// 멤버십 종료일 확인. 종료일은 날짜 단위라 상태가 바뀌는 시점은 자정뿐이므로,
    /// 날짜가 바뀌었을 때만 서버에 확인한다. 같은 날에는 타이머가 깨어나도 요청을 보내지 않는다.
    /// 스레드풀 타이머에서 호출되며, 끝날 때마다 다음 실행을 다시 예약한다.
    /// </summary>
    private async Task SendSessionHeartbeatAsync()
    {
        if (_authenticationClient is null || _authenticationSession is null) return;
        if (_sessionTerminated || _lifetime.IsCancellationRequested) return;

        // 날짜가 그대로면 멤버십 상태도 그대로다. 서버에 접속하지 않고 다음 확인만 예약한다.
        if (DateTime.Now.Date <= _lastMembershipCheckedDate)
        {
            ScheduleNextHeartbeat(true);
            return;
        }

        var succeeded = false;
        try
        {
            // 응답이 멈춰도 다음 확인이 밀리지 않도록 1회 대기 시간을 제한한다.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            attempt.CancelAfter(HeartbeatRequestTimeout);
            var result = await _authenticationClient.HeartbeatAsync(_authenticationSession, attempt.Token);
            if (result.Success)
            {
                succeeded = true;
                _lastMembershipCheckedDate = DateTime.Now.Date;
                // 관리자가 네이버 인증값을 교체하면 이 확인에서 새 값을 받아 반영한다.
                NaverCredentialApplier.Apply(_apiConfiguration, result.NaverCredentials);
                if (result.Notices is not null) RunOnUi(() => ApplyNotices(result.Notices));
                return;
            }
            if (!IsFatalSessionError(result.Code))
            {
                ReportHeartbeatFailure(result.Message);
                return;
            }

            HandleFatalSessionError(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application is closing.
        }
        catch (Exception ex)
        {
            ReportHeartbeatFailure(ex is OperationCanceledException
                ? $"응답이 {HeartbeatRequestTimeout.TotalSeconds:0}초를 넘었습니다."
                : ex.Message);
        }
        finally
        {
            if (!_sessionTerminated && !_lifetime.IsCancellationRequested)
                ScheduleNextHeartbeat(succeeded);
        }
    }

    private void ApplyNotices(IReadOnlyList<string> notices)
    {
        if (_settings.Notices.SequenceEqual(notices)) return;
        _settings.Notices = notices.ToList();
        _store.SaveSettings(_settings);
        PopulateNotices();
    }

    private void ReportHeartbeatFailure(string message) =>
        RunOnUi(() =>
        {
            if (Visible)
                SetStatus($"로그인 접속 확인 실패: {message} · " +
                          $"{HeartbeatRetryDelay.TotalSeconds:0}초 뒤 재시도합니다.");
        });

    /// <summary>
    /// 세션이 서버에서 닫힌 경우. 접속 확인을 멈추고 사유를 알린 뒤 로그인 화면으로 돌려보낸다.
    /// 프로그램을 완전히 끄지 않으므로 조치 후 바로 다시 로그인할 수 있다.
    /// </summary>
    private void HandleFatalSessionError(AuthenticationResult result)
    {
        if (_sessionTerminated) return;
        _sessionTerminated = true;
        StopSessionHeartbeat();
        RunOnUi(() =>
        {
            ShowWindow();
            var code = string.IsNullOrWhiteSpace(result.Code) ? "UNKNOWN" : result.Code;
            MessageBox.Show(
                this,
                $"{result.Message}\n(사유 코드: {code})\n로그아웃하고 로그인 화면으로 이동합니다.",
                "로그인 세션 종료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            ReturnToLogin = true;
            ExitApplication();
        });
    }

    /// <summary>성공하면 정상 주기, 실패하면 만료 전에 다시 시도하도록 짧은 간격으로 예약한다.</summary>
    /// <summary>
    /// 다음 확인 시각을 예약한다. 성공했거나 아직 같은 날이면 다음 점검 주기로,
    /// 실패했으면 짧은 간격으로 다시 시도한다.
    /// </summary>
    private void ScheduleNextHeartbeat(bool succeeded)
    {
        var delay = succeeded
            ? (int)MembershipCheckPollInterval.TotalMilliseconds
            : (int)HeartbeatRetryDelay.TotalMilliseconds;
        try
        {
            _sessionHeartbeatTimer?.Change(delay, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 종료 중.
        }
    }

    private void StopSessionHeartbeat()
    {
        try
        {
            _sessionHeartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 종료 중.
        }
    }

    /// <summary>스레드풀에서 올라온 결과를 UI 스레드로 넘긴다. 창이 닫히는 중이면 무시한다.</summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsFatalSessionError(string? code) => code is
        "SESSION_EXPIRED" or
        "INVALID_SESSION" or
        "MEMBER_NOT_FOUND" or
        "MEMBERSHIP_EXPIRED" or
        "PC_LIMIT";

    private static string PriceRange(RankingResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SameAddressMinPrice) && string.IsNullOrWhiteSpace(result.SameAddressMaxPrice))
            return "정상";
        return $"가격 {result.SameAddressMinPrice} ~ {result.SameAddressMaxPrice}";
    }

    /// <summary>단지명. 매물 목록 API의 articleName(단지명)을 그대로 쓴다.</summary>
    private static string ComplexNameDisplay(Listing listing) =>
        string.IsNullOrWhiteSpace(listing.ArticleName) ? "-" : listing.ArticleName.Trim();

    private static string PropertyTypeDisplay(Listing listing)
    {
        if (!string.IsNullOrWhiteSpace(listing.RealEstateType)) return listing.RealEstateType;
        return listing.ArticleName switch
        {
            "아파트" or "오피스텔" or "빌라" or "연립" or "다세대" or "단독주택" or
                "다가구주택" or "원룸" or "상가" or "사무실" or "공장" or "창고" or
                "토지" or "분양권" or "재개발" or "재건축" => listing.ArticleName,
            _ => "-"
        };
    }

    private static string ListingNameDisplay(Listing listing)
    {
        if (string.IsNullOrWhiteSpace(listing.Address))
            return string.Join(" · ", new[] { listing.Location, listing.ArticleName, listing.BuildingName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(listing.Description)) return listing.Address;

        // 이전 버전 캐시의 Address에는 설명이 합쳐져 있으므로 화면 표시 시 정확히 분리한다.
        return string.Join(" · ", listing.Address
            .Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.Equals(part, listing.Description, StringComparison.Ordinal)));
    }

    private string PropertyAnalysisDisplay(Listing listing)
    {
        return "분석";
    }

    private static string JoinDetails(Listing listing) =>
        string.Join(" · ", new[] { listing.Area, listing.FloorInfo }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private sealed record GridRowTag(RankingResult Result, Listing Listing, bool IsChild);
}
