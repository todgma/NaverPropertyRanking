using System.Diagnostics;
using System.Net;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class MainForm : Form
{
    private readonly LocalStore _store;
    private readonly NaverLandClient _apiClient;
    private readonly ApiConfiguration _apiConfiguration;
    private readonly GoogleAuthenticationClient? _authenticationClient;
    private readonly AuthenticationSession? _authenticationSession;
    private readonly System.Windows.Forms.Timer? _sessionHeartbeatTimer;
    private readonly TextBox _groupId = new() { Width = 210, PlaceholderText = "네이버 부동산 단체 ID" };
    private readonly TextBox _articleNumbers = new() { Width = 280, PlaceholderText = "예: 2612345678, 2612345679" };
    private readonly CheckBox _saveGroupId = new() { Text = "저장", AutoSize = true, Checked = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _loadButton = new() { Text = "매물목록조회", Width = 125, Height = 32 };
    private readonly Button _refreshButton = new() { Text = "순위조회", Width = 100, Height = 32, Enabled = false };
    private readonly Button _settingsButton = new() { Text = "설정", Width = 75, Height = 32 };
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
    private readonly ToolStripStatusLabel _status = new() { Text = "준비" };
    private readonly ToolStripStatusLabel _lastChecked = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _cooldownUiTimer = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _noticeRotationTimer = new() { Interval = 60_000 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _expanded = [];
    private readonly HashSet<string> _selectedArticleNumbers = [];
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
    private readonly HashSet<string> _lastCompletedRankingTargets = [];
    private bool _hasLoadedListings;
    private readonly List<RankingNotificationForm> _notificationPopups = [];
    private bool _heartbeatRunning;
    private bool _startingListingWorkflow;
    private string? _loadedListingLoginId;
    private string? _loadedListingGroupId;
    private DateTime? _restoredListingCacheAtUtc;
    private ListingSortOrder _listingSortOrder = ListingSortOrder.DuplicateCountDescending;
    private bool _startupCacheSynchronizationStarted;

    public MainForm(
        LocalStore store,
        NaverLandClient apiClient,
        AppSettings settings,
        ApiConfiguration apiConfiguration,
        AuthenticationSession? authenticationSession = null,
        GoogleAuthenticationClient? authenticationClient = null,
        GoogleAuthenticationConfiguration? authenticationConfiguration = null)
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
        WireEvents();
        ApplySettingsToUi();
        RestoreListingCache();
        PopulateNotices();
        ConfigureTimer();
        UpdateCooldownUi();
        if (_authenticationClient is not null && _authenticationSession is not null)
        {
            _sessionHeartbeatTimer = new System.Windows.Forms.Timer
            {
                Interval = checked(Math.Clamp(
                    authenticationConfiguration?.HeartbeatIntervalSeconds ?? 120,
                    30,
                    240) * 1000)
            };
            _sessionHeartbeatTimer.Tick += async (_, _) => await SendSessionHeartbeatAsync();
            _sessionHeartbeatTimer.Start();
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
            ColumnCount = 7,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = header.BackColor
        };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
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
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        titleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "네이버 매물 순위",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("맑은 고딕", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        var noticeTitle = new Label
        {
            Text = "공지사항",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = Padding.Empty
        };
        _noticePanel.Margin = new Padding(0, 5, 0, 5);
        _noticePanel.Controls.Add(_noticeText);
        _noticePanel.Controls.Add(_noticeScroll);
        titleLayout.Controls.Add(title, 0, 0);
        titleLayout.Controls.Add(noticeTitle, 1, 0);
        titleLayout.Controls.Add(_noticePanel, 2, 0);
        searchLayout.Controls.Add(titleLayout, 0, 0);
        searchLayout.SetColumnSpan(titleLayout, 7);

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

        foreach (var button in new[] { _loadButton, _refreshButton, _settingsButton })
        {
            button.Dock = DockStyle.None;
            button.Anchor = AnchorStyles.None;
            button.Margin = Padding.Empty;
        }

        searchLayout.Controls.Add(groupLabel, 0, 1);
        searchLayout.Controls.Add(_groupId, 1, 1);
        searchLayout.Controls.Add(_saveGroupId, 2, 1);
        searchLayout.Controls.Add(_loadButton, 4, 1);
        searchLayout.Controls.Add(_refreshButton, 5, 1);
        searchLayout.Controls.Add(_settingsButton, 6, 1);
        header.Controls.Add(searchLayout);

        ConfigureGrid();
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var sortPanel = BuildSortPanel();
        var pagingPanel = BuildPagingPanel();
        content.Controls.Add(_grid);
        content.Controls.Add(pagingPanel);
        content.Controls.Add(sortPanel);
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_lastChecked);

        Controls.Add(content);
        Controls.Add(header);
        Controls.Add(statusStrip);
    }

    private Control BuildSortPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(12, 8, 0, 0),
            BackColor = Color.FromArgb(247, 250, 249)
        };
        panel.Controls.Add(new Label
        {
            Text = "검색조건",
            AutoSize = true,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Margin = new Padding(0, 4, 12, 0)
        });
        _excludeSingleListings.Margin = new Padding(0, 3, 28, 0);
        panel.Controls.Add(_excludeSingleListings);
        panel.Controls.Add(new Label
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
            panel.Controls.Add(option);
        }
        return panel;
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
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "☐ 전체", Width = 72, ThreeState = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Expand", HeaderText = "", Width = 42, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mine", HeaderText = "구분", Width = 65 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ArticleNo", HeaderText = "네이버 매물번호", Width = 135 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Address", HeaderText = "매물종류/설명", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Trade", HeaderText = "거래유형", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "거래금액", Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PreviousRank", HeaderText = "이전랭킹", Width = 82 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CurrentRank", HeaderText = "현재랭킹", Width = 105 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "동일매물", Width = 85 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Realtor", HeaderText = "공인중개사", Width = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Provider", HeaderText = "정보제공", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "상태", Width = 180 });
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
            Text = "네이버 매물 순위",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private void WireEvents()
    {
        _loadButton.Click += async (_, _) => await StartListingWorkflowAsync();
        _refreshButton.Click += async (_, _) => await RefreshSelectedRankingsAsync(false);
        _settingsButton.Click += (_, _) => OpenSettings();
        _timer.Tick += async (_, _) =>
        {
            if (_hasLoadedListings) await RefreshSelectedRankingsAsync(true);
        };
        _cooldownUiTimer.Tick += (_, _) => UpdateCooldownUi();
        _noticeRotationTimer.Tick += (_, _) => AdvanceNotice();
        _grid.CellClick += GridOnCellClick;
        _grid.ColumnHeaderMouseClick += GridOnColumnHeaderMouseClick;
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
        FormClosing += OnFormClosing;
    }

    private void ApplyListingVisibilityFilter()
    {
        _currentPage = 1;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        var visibleCount = VisibleListings().Count;
        SetStatus(_excludeSingleListings.Checked
            ? $"단일매물을 제외했습니다. 표시 {visibleCount}건 / 전체 {_ownListings.Count}건"
            : $"단일매물 제외를 해제했습니다. 전체 {_ownListings.Count}건 표시");
    }

    private void ApplyListingSort(RadioButton option, ListingSortOrder sortOrder)
    {
        if (!option.Checked) return;
        _listingSortOrder = sortOrder;
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

        if (!ShowSettingsDialog()) return;
        _startingListingWorkflow = true;
        SetListingLoadBusy(true);
        SetStatus("회원_단체 정보를 확인하는 중...");
        try
        {
            if (!await EnsureMemberGroupAsync(_settings.GroupId)) return;
        }
        finally
        {
            _startingListingWorkflow = false;
            SetListingLoadBusy(false);
        }
        await RefreshAllAsync(false);
    }

    private async Task TrayRefreshAsync()
    {
        if (!_hasLoadedListings)
        {
            ShowWindow();
            SetStatus("단체 ID 입력 후 매물목록조회를 먼저 실행해 주세요.");
            return;
        }
        await RefreshSelectedRankingsAsync(false);
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

    private async Task RefreshAllAsync(bool isAutomatic)
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
            if (!isAutomatic) MessageBox.Show(this, _status.Text, "입력 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        if (_settings.RateLimitBlockedUntilUtc is { } blockedUntil && blockedUntil > DateTime.UtcNow)
        {
            HandleRateLimit(blockedUntil);
            return;
        }

        if (HasDisplayedListingsForCurrentIdentity())
        {
            await SynchronizeDisplayedListingsAsync(isAutomatic);
            return;
        }

        _refreshing = true;
        SetListingLoadBusy(true);
        SetStatus("내 매물 목록을 불러오는 중...");
        try
        {
            _store.SaveSettings(_settings);
            _ownListings = [];
            _rankingCache.Clear();
            _selectedArticleNumbers.Clear();
            _expanded.Clear();
            _results = [];
            _currentPage = 1;
            _hasLoadedListings = false;
            _loadedListingLoginId = null;
            _loadedListingGroupId = null;
            RenderGrid();

            var listingProgress = new Progress<ListingLoadProgress>(progress =>
                SetStatus($"매물 목록 {progress.ListingCount}건 수집 중 · {progress.Page}페이지"));
            var loadedListings = await _apiClient.GetOwnListingsAsync(
                _settings,
                _lifetime.Token,
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
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
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
        SetBusy(true);
        try
        {
            await RankCurrentPageCoreAsync(forceRefresh);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"랭킹 조회 실패: {ex.Message}");
            if (!isAutomatic) MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            SetBusy(false);
        }
    }

    private async Task RefreshSelectedRankingsAsync(bool isAutomatic)
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

        var targets = RankingTargetSelector.Select(_ownListings, _selectedArticleNumbers);
        var refreshAll = targets.Count == _ownListings.Count;
        var scope = refreshAll ? $"전체 {_ownListings.Count}건" : $"선택 {targets.Count}건";

        _refreshing = true;
        SetBusy(true);
        try
        {
            await RankListingsCoreAsync(targets, true, scope);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"랭킹 조회 실패: {ex.Message}");
            if (!isAutomatic) MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            SetBusy(false);
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
        var pendingListings = new Queue<Listing>(ListingSorter
            .Sort(targetListings, _rankingCache, _listingSortOrder)
            .Where(listing => forceRefresh || !_rankingCache.ContainsKey(listing.ArticleNo)));
        var requestCount = pendingListings.Count;
        var completedCount = 0;
        UpdateCurrentPageResults("랭킹 조회 대기");
        RenderGrid();
        ConfigureBusyProgress(requestCount);

        var runningRequests = new Dictionary<Task<RankingResult>, Listing>();
        var previousRanks = new Dictionary<string, int?>(StringComparer.Ordinal);
        var stopLaunching = false;

        void LaunchAvailableRequests()
        {
            while (!stopLaunching && runningRequests.Count < 5 && pendingListings.Count > 0)
            {
                var listing = pendingListings.Dequeue();
                int? previousRank = null;
                if (_rankingCache.TryGetValue(listing.ArticleNo, out var cached) && cached.Success)
                    previousRank = cached.Rank;
                else if (_snapshots.TryGetValue(listing.ArticleNo, out var savedSnapshot))
                    previousRank = savedSnapshot.Rank;

                previousRanks[listing.ArticleNo] = previousRank;
                var request = _apiClient.GetRankingAsync(listing, ownNumbers, _settings, _lifetime.Token);
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
            string? lastCompletedArticleNo = null;

            foreach (var completedTask in completedTasks)
            {
                var listing = runningRequests[completedTask];
                runningRequests.Remove(completedTask);
                completedCount++;
                lastCompletedArticleNo = listing.ArticleNo;
                var result = (await completedTask) with
                {
                    PreviousRank = previousRanks[listing.ArticleNo]
                };
                attemptedResults.Add(result);
                _rankingCache[listing.ArticleNo] = result;

                if (result.Success)
                {
                    _snapshots.TryGetValue(listing.ArticleNo, out var previous);
                    var comparison = RankingAnalyzer.Compare(result, previous, _settings);
                    _snapshots[listing.ArticleNo] = comparison.Snapshot;
                    allEvents.AddRange(comparison.Events);
                }
                if (result.Error?.Contains("429", StringComparison.Ordinal) == true)
                    stopLaunching = true;
            }

            ReportBusyProgress(completedCount, requestCount, $"{scope} 랭킹 조회 중\n{completedCount}/{requestCount} 완료");
            SetStatus($"{scope} 랭킹 조회 중... {completedCount}/{requestCount} ({lastCompletedArticleNo})");
            UpdateCurrentPageResults("랭킹 조회 중");
            RenderGrid();
            LaunchAvailableRequests();
        }

        UpdateCurrentPageResults("랭킹 미조회");
        _store.SaveSnapshots(_snapshots);
        _store.SaveSettings(_settings);
        RenderGrid();
        SaveCurrentListingCache();
        _lastChecked.Text = $"마지막 조회: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

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

        SetStatus($"로컬 매물 {_ownListings.Count}건 표시 완료 · 매물목록조회 기능을 자동 실행합니다.");
        if (!await EnsureMemberGroupAsync(_settings.GroupId)) return;

        // 시작 시에도 사용자가 같은 단체 ID로 매물목록조회를 누른 것과 동일하게 처리한다.
        // 기존 순위 조회와 최신 목록 API를 별도 Task로 시작하고, API 결과를 기준으로
        // 신규 항목 추가 및 종료 항목 삭제까지 동일한 동기화 경로에서 수행한다.
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
        SetListingLoadBusy(true);
        SetStatus($"현재 매물 {_ownListings.Count}건 순위 조회와 최신 목록 확인을 시작합니다.");

        try
        {
            _store.SaveSettings(_settings);
            var displayedListings = ListingSorter
                .Sort(_ownListings.ToList(), _rankingCache, _listingSortOrder)
                .ToList();
            var listingProgress = new Progress<ListingLoadProgress>(progress =>
                SetStatus($"매물목록조회 중 · 최신 목록 {progress.ListingCount}건/{progress.Page}페이지 · 기존 순위 조회 병행"));

            // 최신 목록 API와 현재 화면 매물의 순위 API를 독립 Task로 동시에 시작한다.
            // 두 API는 각각 전용 요청 게이트와 호출 간격 제한을 사용한다.
            var latestListingTask = _apiClient.GetOwnListingsAsync(
                _settings,
                _lifetime.Token,
                listingProgress);
            var displayedRankingTask = RankListingsCoreAsync(
                displayedListings,
                true,
                $"기존 매물 {displayedListings.Count}건",
                false);

            IReadOnlyList<Listing> latestListings;
            try
            {
                latestListings = await latestListingTask;
            }
            catch
            {
                await displayedRankingTask;
                throw;
            }

            var reconciliation = ListingCollectionMerger.Reconcile(_ownListings, latestListings);
            var removedNumbers = reconciliation.RemovedListings
                .Select(listing => listing.ArticleNo)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var articleNo in removedNumbers)
            {
                _rankingCache.Remove(articleNo);
                _snapshots.Remove(articleNo);
                _selectedArticleNumbers.Remove(articleNo);
                _expanded.Remove(articleNo);
                _lastCompletedRankingTargets.Remove(articleNo);
            }

            _ownListings = reconciliation.Listings.ToList();
            _hasLoadedListings = _ownListings.Count > 0;
            var ownNumbers = _ownListings
                .Select(listing => listing.ArticleNo)
                .ToHashSet(StringComparer.Ordinal);
            NormalizeLoadedRankingOwnership(ownNumbers);
            RefreshRankingOwnListingDetails();
            UpdateCurrentPageResults(reconciliation.AddedListings.Count > 0
                ? "신규 매물 순위 조회 대기"
                : "랭킹 미조회");
            RenderGrid();
            _store.SaveSnapshots(_snapshots);
            SaveCurrentListingCache();

            // 최신 목록 비교 결과는 기존 순위 조회 완료를 기다리지 않고 즉시 바인딩한다.
            // 신규 순위 작업도 바로 등록되며, 순위 API 전용 게이트 안에서 기존 순위 작업과
            // 안전하게 순서를 나눠 실행된다.
            var addedRankingTask = reconciliation.AddedListings.Count > 0 &&
                                   !(_settings.RateLimitBlockedUntilUtc is { } rateLimitUntil && rateLimitUntil > DateTime.UtcNow)
                ? RankListingsCoreAsync(
                    ListingSorter.Sort(reconciliation.AddedListings, _rankingCache, _listingSortOrder).ToList(),
                    true,
                    $"신규 매물 {reconciliation.AddedListings.Count}건",
                    false)
                : Task.FromResult(RankingBatchSummary.Empty);

            var displayedSummary = await displayedRankingTask;
            var addedSummary = await addedRankingTask;

            // API 결과에서 삭제된 매물의 기존 순위 작업이 이미 진행 중이었을 수 있으므로
            // 두 순위 작업 완료 후 관련 캐시와 스냅샷을 한 번 더 정리한다.
            foreach (var articleNo in removedNumbers)
            {
                _rankingCache.Remove(articleNo);
                _snapshots.Remove(articleNo);
            }
            _store.SaveSnapshots(_snapshots);

            SetLoadedListingIdentity(_settings.GroupId);
            SaveCurrentListingCache();
            var successCount = displayedSummary.SuccessCount + addedSummary.SuccessCount;
            var failureCount = displayedSummary.FailureCount + addedSummary.FailureCount;
            var events = displayedSummary.Events.Concat(addedSummary.Events).ToList();
            var scope = $"최신 {_ownListings.Count}건 · 신규 {reconciliation.AddedListings.Count}건 · 삭제 {reconciliation.RemovedListings.Count}건";
            SetStatus($"완료: {scope} · 순위 성공 {successCount}건 / 실패 {failureCount}건");
            ShowRankingCompletionPopup(scope, successCount, failureCount, events);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
        }
        catch (NaverApiException ex)
        {
            _store.SaveSettings(_settings);
            SetStatus($"기존 목록은 유지했습니다. 매물목록조회 실패: {ex.Message}");
            if (ex.StatusCode == HttpStatusCode.TooManyRequests)
                HandleRateLimit(_settings.RateLimitBlockedUntilUtc ?? DateTime.UtcNow.AddMinutes(30));
            else if (!isAutomatic)
                MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            SaveCurrentListingCache();
            SetStatus($"기존 목록은 유지했습니다. 매물목록조회 실패: {ex.Message}");
            if (!isAutomatic)
                MessageBox.Show(this, ex.Message, "조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
            SetListingLoadBusy(false);
        }
    }

    private void RefreshRankingOwnListingDetails()
    {
        foreach (var listing in _ownListings)
        {
            if (!_rankingCache.TryGetValue(listing.ArticleNo, out var result)) continue;
            _rankingCache[listing.ArticleNo] = result with
            {
                OwnListing = listing with { IsMine = true }
            };
        }
    }

    private sealed record RankingBatchSummary(
        int SuccessCount,
        int FailureCount,
        IReadOnlyList<NotificationEvent> Events)
    {
        public static RankingBatchSummary Empty { get; } = new(0, 0, []);
    }

    private List<Listing> CurrentPageListings()
    {
        var sortedListings = ListingSorter.Sort(VisibleListings(), _rankingCache, _listingSortOrder);
        return ListingPagination.GetPage(sortedListings, _currentPage, _settings.DisplayPageSize).ToList();
    }

    private IReadOnlyList<Listing> VisibleListings() =>
        ListingVisibilityFilter.Apply(_ownListings, _rankingCache, _excludeSingleListings.Checked);

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
        UpdateSelectionHeader();
        UpdatePagingControls();
    }

    private void AddParentRow(RankingResult result)
    {
        var listing = result.OwnListing;
        var expandable = result.Comparables.Count > 0;
        var rowIndex = _grid.Rows.Add(
            _selectedArticleNumbers.Contains(listing.ArticleNo),
            expandable ? (_expanded.Contains(listing.ArticleNo) ? "▼" : "▶") : string.Empty,
            "내 매물",
            listing.ArticleNo,
            listing.Address,
            listing.TradeType,
            listing.Price,
            RankPresentation.FormatPrevious(result.PreviousRank),
            RankPresentation.FormatCurrent(result.PreviousRank, result.Rank),
            result.Success ? $"{result.Total}건" : "-",
            listing.RealtorName,
            listing.ProviderName,
            result.Success ? PriceRange(result) : result.Error);
        var row = _grid.Rows[rowIndex];
        row.Tag = new GridRowTag(result, listing, false);
        row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
        row.DefaultCellStyle.BackColor = Color.FromArgb(244, 250, 247);
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
    }

    private void AddComparableRow(RankingResult result, Listing listing)
    {
        var rowIndex = _grid.Rows.Add(
            null,
            string.Empty,
            listing.IsMine ? "내 매물" : "동일매물",
            listing.ArticleNo,
            "    " + listing.Address,
            listing.TradeType,
            listing.Price,
            string.Empty,
            result.Comparables.ToList().FindIndex(x => x.ArticleNo == listing.ArticleNo) + 1 + "위",
            string.Empty,
            listing.RealtorName,
            listing.ProviderName,
            JoinDetails(listing));
        var row = _grid.Rows[rowIndex];
        row.Tag = new GridRowTag(result, listing, true);
        row.Cells["Selected"].ReadOnly = true;
        row.DefaultCellStyle.BackColor = listing.IsMine ? Color.FromArgb(232, 247, 239) : Color.White;
        row.DefaultCellStyle.ForeColor = listing.IsMine ? Color.FromArgb(0, 105, 62) : Color.FromArgb(55, 55, 55);
    }

    private void GridOnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        if (_grid.Rows[e.RowIndex].Tag is not GridRowTag { IsChild: false } tag) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;
        if (columnName == "Selected")
        {
            if (!_selectedArticleNumbers.Add(tag.Listing.ArticleNo))
                _selectedArticleNumbers.Remove(tag.Listing.ArticleNo);
            _grid.Rows[e.RowIndex].Cells["Selected"].Value = _selectedArticleNumbers.Contains(tag.Listing.ArticleNo);
            UpdateSelectionHeader();
            SetStatus($"랭킹 재조회 선택: {_selectedArticleNumbers.Count}/{_ownListings.Count}건");
            return;
        }
        if (columnName != "Expand") return;
        if (tag.Result.Comparables.Count == 0) return;

        if (!_expanded.Add(tag.Listing.ArticleNo)) _expanded.Remove(tag.Listing.ArticleNo);
        RenderGrid();
    }

    private void GridOnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Selected" || _ownListings.Count == 0) return;

        if (_selectedArticleNumbers.Count == _ownListings.Count)
        {
            _selectedArticleNumbers.Clear();
        }
        else
        {
            _selectedArticleNumbers.Clear();
            foreach (var listing in _ownListings) _selectedArticleNumbers.Add(listing.ArticleNo);
        }
        RenderGrid();
        SetStatus($"랭킹 재조회 선택: {_selectedArticleNumbers.Count}/{_ownListings.Count}건");
    }

    private void UpdateSelectionHeader()
    {
        if (!_grid.Columns.Contains("Selected")) return;
        var allSelected = _ownListings.Count > 0 && _selectedArticleNumbers.Count == _ownListings.Count;
        _grid.Columns["Selected"].HeaderText = allSelected ? "☑ 전체" : "☐ 전체";
    }

    private void GridOnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not GridRowTag tag) return;
        if (_grid.Columns[e.ColumnIndex].Name is "Selected" or "Expand") return;
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

    private void OpenSettings()
    {
        ShowSettingsDialog();
    }

    private bool ShowSettingsDialog()
    {
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
        SetStatus("설정을 저장했습니다. API 헤더는 appsettings.json에서 관리합니다.");
        return true;
    }

    private void ConfigureTimer()
    {
        _timer.Stop();
        _timer.Interval = checked(_settings.PollIntervalMinutes * 60 * 1000);
        if (_settings.AutoRefresh) _timer.Start();
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

        _selectedArticleNumbers.Clear();
        _expanded.Clear();
        _currentPage = 1;
        _hasLoadedListings = _ownListings.Count > 0;
        _loadedListingLoginId = cache.LoginId;
        _loadedListingGroupId = cache.GroupId;
        _restoredListingCacheAtUtc = cache.SavedAtUtc;
        UpdateCurrentPageResults("랭킹 미조회");
        RenderGrid();
        _lastChecked.Text = $"로컬 저장: {cache.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
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
        const string applicationTitle = "네이버 매물 순위";
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

    private void ShowRankingCompletionPopup(
        string scope,
        int successCount,
        int failureCount,
        IReadOnlyList<NotificationEvent> events)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
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
        var refreshCheckedListingsInBackground = RankingTargetSelector.ShouldRefreshOnClose(
            _ownListings,
            _selectedArticleNumbers,
            _lastCompletedRankingTargets,
            _lastRankingCompletedUtc,
            DateTime.UtcNow);
        Hide();
        ShowInTaskbar = false;
        if (refreshCheckedListingsInBackground)
            BeginInvoke(async () => await RefreshSelectedRankingsAsync(true));
        ShowBackgroundTip(refreshCheckedListingsInBackground
            ? "선택한 매물의 랭킹을 백그라운드에서 조회합니다."
            : "랭킹 모니터가 시스템 트레이에서 계속 실행됩니다.");
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
        _timer.Stop();
        _sessionHeartbeatTimer?.Stop();
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
            _sessionHeartbeatTimer?.Dispose();
            _timer.Dispose();
            _cooldownUiTimer.Dispose();
            _noticeRotationTimer.Dispose();
            _trayIcon.Dispose();
            _busyOverlay.Dispose();
            _applicationIcon.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SetBusy(bool busy)
    {
        var blocked = _settings.RateLimitBlockedUntilUtc is { } until && until > DateTime.UtcNow;
        _loadButton.Enabled = !busy && !blocked;
        _refreshButton.Enabled = !busy && !blocked && _hasLoadedListings;
        _settingsButton.Enabled = !busy;
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
        _loadButton.Enabled = !busy && !blocked;
        _refreshButton.Enabled = !busy && !blocked && _hasLoadedListings;
        _settingsButton.Enabled = !busy;
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
        _status.Text = text;
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

    private async Task SendSessionHeartbeatAsync()
    {
        if (_heartbeatRunning || _authenticationClient is null || _authenticationSession is null) return;
        _heartbeatRunning = true;
        try
        {
            var result = await _authenticationClient.HeartbeatAsync(_authenticationSession, _lifetime.Token);
            if (result.Success)
            {
                if (result.Notices is not null && !_settings.Notices.SequenceEqual(result.Notices))
                {
                    _settings.Notices = result.Notices.ToList();
                    _store.SaveSettings(_settings);
                    PopulateNotices();
                }
                return;
            }
            if (!IsFatalSessionError(result.Code))
            {
                if (Visible) SetStatus($"로그인 접속 확인 실패: {result.Message} · 다음 주기에 재시도합니다.");
                return;
            }

            _sessionHeartbeatTimer?.Stop();
            ShowWindow();
            MessageBox.Show(
                this,
                result.Message + "\n프로그램을 종료합니다.",
                "로그인 세션 종료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            ExitApplication();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Application is closing.
        }
        catch (Exception ex)
        {
            if (Visible) SetStatus($"로그인 접속 확인 실패: {ex.Message} · 다음 주기에 재시도합니다.");
        }
        finally
        {
            _heartbeatRunning = false;
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

    private static string JoinDetails(Listing listing) =>
        string.Join(" · ", new[] { listing.Area, listing.FloorInfo }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private sealed record GridRowTag(RankingResult Result, Listing Listing, bool IsChild);
}
