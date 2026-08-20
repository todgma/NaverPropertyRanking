using System.Diagnostics;
using System.Net;
using System.Windows.Forms;
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
    private readonly string _currentVersion;
    private readonly TextBox _groupId = new() { Width = 150, PlaceholderText = "네이버 부동산 단체 ID" };
    private readonly TextBox _articleNumbers = new() { Width = 150, PlaceholderText = "예: 2612345678, 2612345679" };
    private readonly CheckBox _saveGroupId = new() { Text = "저장", AutoSize = true, Checked = true, Padding = new Padding(0, 6, 0, 0), Visible = false };
    private readonly Button _loadButton = new() { Text = "매물동기화", Width = 125, Height = 32 };
    private readonly Button _refreshButton = new() { Text = "순위조회", Width = 100, Height = 32, Enabled = false };
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
    private readonly HashSet<string> _propertyAnalysisInProgress = [];
    private readonly HashSet<string> _failedRankingArticleNumbers = [];
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
    private bool _heartbeatRunning;
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
        GoogleAuthenticationConfiguration? authenticationConfiguration = null,
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
            ColumnCount = 9,
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
        searchLayout.SetColumnSpan(titleLayout, 9);

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
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.FromArgb(247, 250, 249)
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
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
        _excludeSingleListings.Margin = new Padding(0, 3, 28, 0);
        optionsPanel.Controls.Add(_excludeSingleListings);
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
        container.Controls.Add(optionsPanel, 0, 0);
        container.Controls.Add(_excelExportButton, 1, 0);
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

            // 저장된 순서에 조회결과 컬럼이 없으면(기존 사용자) 이전순위 바로 앞에 배치한다.
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
        _loadButton.Click += async (_, _) => await StartListingWorkflowAsync();
        _refreshButton.Click += async (_, _) => await RefreshAllRankingsAsync(false);
        _retryFailedRankingsButton.Click += async (_, _) => await RetryFailedRankingsAsync();
        _advertisementAnalysisButton.Click += async (_, _) => await ShowOwnedComplexListPopupAsync();
        _settingsButton.Click += (_, _) => OpenSettings();
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
            EnsureAutoRetryFailedRankingsLoop();
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
        SetBusy(true);
        try
        {
            await RankListingsCoreAsync(_ownListings, true, scope);
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
        }
        catch (Exception ex)
        {
            SetStatus($"실패 매물 재조회 오류: {ex.Message}");
            MessageBox.Show(this, ex.Message, "재조회 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _refreshing = false;
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
                _refreshing = true;
                SetBusy(true);
                try
                {
                    var scope = $"실패 매물 자동 재조회 {attempt}회차 {targets.Count}건";
                    summary = await RankListingsCoreAsync(targets, true, scope, false);
                }
                finally
                {
                    _refreshing = false;
                    SetBusy(false);
                }

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
                if (_settings.PropertyAnalysisEnabled)
                    _propertyAnalysisInProgress.Add(listing.ArticleNo);
                var request = _apiClient.GetRankingAsync(listing, ownNumbers, _settings, _lifetime.Token);
                runningRequests.Add(request, listing);
            }
        }

        LaunchAvailableRequests();
        if (_settings.PropertyAnalysisEnabled) RenderGrid();
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
                _propertyAnalysisInProgress.Remove(listing.ArticleNo);
                completedCount++;
                lastCompletedArticleNo = listing.ArticleNo;
                var result = (await completedTask) with
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

        foreach (var listing in targetListings)
            _propertyAnalysisInProgress.Remove(listing.ArticleNo);

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
        SetListingLoadBusy(true);
        SetStatus($"현재 매물 {_ownListings.Count}건을 기준으로 최신 매물목록을 다시 조회합니다.");

        try
        {
            _store.SaveSettings(_settings);
            var listingProgress = new Progress<ListingLoadProgress>(progress =>
                SetStatus($"매물목록 재조회 중 · 최신 목록 {progress.ListingCount}건 · {progress.Page}페이지"));
            var latestListings = await _apiClient.GetOwnListingsAsync(
                _settings,
                _lifetime.Token,
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
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
        UpdatePagingControls();
        _excelExportButton.Enabled = !_refreshing && _results.Count > 0;
    }

    private void AddParentRow(RankingResult result)
    {
        var listing = result.OwnListing;
        var expandable = result.Comparables.Count > 0;
        var rowIndex = _grid.Rows.Add(
            expandable ? (_expanded.Contains(listing.ArticleNo) ? "▼" : "▶") : string.Empty,
            "내 매물",
            listing.ArticleNo,
            PropertyTypeDisplay(listing),
            ListingNameDisplay(listing),
            listing.TradeType,
            listing.Price,
            QueryResultDisplay(result),
            RankPresentation.FormatPrevious(result.PreviousRank),
            RankPresentation.FormatCurrent(result.PreviousRank, result.Rank),
            result.Success ? $"{result.Total}건" : "-",
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
        row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
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
            listing.IsMine ? "내 매물" : "동일매물",
            $"└ {listing.ArticleNo}",
            PropertyTypeDisplay(listing),
            "    " + ListingNameDisplay(listing),
            listing.TradeType,
            listing.Price,
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
            .Where(column => column.Name is not "Expand" and not "ComplexNo")
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
        if (columnName == "PropertyAnalysis")
        {
            await OpenPropertyAnalysisAsync(tag.Listing);
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
        if (_grid.Columns[e.ColumnIndex].Name is "Expand" or "PropertyAnalysis") return;
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
                        _lifetime.Token);
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
            _rankingCache[listing.ArticleNo] = synchronizedResult;
            var comparisonDetails = new List<RankedArticleComparison>(competitors.Count);
            for (var index = 0; index < competitors.Count; index++)
            {
                var competitor = competitors[index];
                comparisonDetails.Add(new RankedArticleComparison(
                    index + 1,
                    competitor.ExposureRank,
                    await LoadDetailAsync(competitor.Listing)));
            }

            var analysis = new AdvertisementListingAnalysis(
                synchronizedResult,
                ownDetail with { Listing = synchronizedResult.OwnListing },
                comparisonDetails);
            SaveCurrentListingCache();
            UpdateCurrentPageResults("랭킹 미조회");
            RenderGrid();
            SetStatus($"물건분석 표시 · {listing.ArticleNo} · 상위 동일매물 {comparisonDetails.Count}건");
            using var dialog = new PropertyAnalysisForm(analysis);
            dialog.ShowDialog(this);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App is closing.
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
    /// 광고분석 버튼: 현재 조회된 내 매물의 단지번호를 중복 없이 추린다.
    /// 단지번호가 비어 있는 매물은 매물 상세 API로 보강하고,
    /// 단지번호별로 단지 정보 API(/api/complexes/{complexNo})를 조회해 팝업으로 보여준다.
    /// </summary>
    private async Task ShowOwnedComplexListPopupAsync()
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
            // 1) 단지번호가 비어 있는 내 매물을 매물 상세 API로 보강한다.
            var hydrateTargets = Enumerable.Range(0, _ownListings.Count)
                .Where(index => string.IsNullOrWhiteSpace(_ownListings[index].ComplexNo) &&
                                !string.IsNullOrWhiteSpace(_ownListings[index].ArticleNo))
                .ToList();
            var hydratedCount = 0;
            for (var i = 0; i < hydrateTargets.Count; i++)
            {
                var index = hydrateTargets[i];
                var listing = _ownListings[index];
                SetStatus($"광고분석 단지번호 확인 중 · {i + 1}/{hydrateTargets.Count} · {listing.ArticleNo}");
                try
                {
                    var hydrated = await _apiClient.HydrateComplexIdentityAsync(
                        listing,
                        _settings,
                        _lifetime.Token);
                    if (string.IsNullOrWhiteSpace(hydrated.ComplexNo)) continue;
                    _ownListings[index] = MergeListingDetails(hydrated, listing);
                    hydratedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 호출 제한이면 남은 보강을 중단하고 확보된 단지번호로 계속 진행한다.
                    if (_settings.RateLimitBlockedUntilUtc is { } cooldown && cooldown > DateTime.UtcNow)
                    {
                        HandleRateLimit(cooldown);
                        break;
                    }
                }
            }
            if (hydratedCount > 0) SaveCurrentListingCache();

            // 2) 단지번호를 중복 없이 그룹핑한다.
            var complexes = AdvertisementAnalysisService.GroupOwnedComplexes(_ownListings);
            if (complexes.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "내 매물 중 단지 정보가 있는 매물이 없습니다.",
                    "광고분석",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // 3) 단지번호별 단지 정보와 광고 상위 중개인을 조회한다.
            var information = new Dictionary<string, ComplexInformation>(StringComparer.Ordinal);
            var advertisementRealtors =
                new Dictionary<string, IReadOnlyList<ComplexAdvertisementRealtor>>(StringComparer.Ordinal);
            for (var i = 0; i < complexes.Count; i++)
            {
                var complex = complexes[i];
                SetStatus($"단지정보 조회 중 · {i + 1}/{complexes.Count} · {complex.ComplexName}");
                information[complex.ComplexNo] = await _apiClient.GetComplexInformationAsync(
                    complex.ComplexNo,
                    complex.ComplexName,
                    _settings,
                    _lifetime.Token);
                advertisementRealtors[complex.ComplexNo] =
                    await _apiClient.GetComplexAdvertisementRealtorsAsync(
                        complex.ComplexNo,
                        _settings,
                        _lifetime.Token);
            }

            SetStatus($"광고분석 · 단지 {complexes.Count}곳 단지정보 조회 완료");
            using var dialog = new OwnedComplexListForm(
                complexes,
                information,
                advertisementRealtors,
                FirstNotEmpty(_loadedListingGroupId, _groupId.Text.Trim(), _settings.GroupId));
            dialog.ShowDialog(this);
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
        UpdateRetryFailedRankingsButton(busy);
        _advertisementAnalysisButton.Enabled = !busy && CanUseAdvertisementAnalysis;
        _settingsButton.Enabled = !busy;
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
        _loadButton.Enabled = !busy && !blocked;
        _refreshButton.Enabled = !busy && !blocked && _hasLoadedListings;
        UpdateRetryFailedRankingsButton(busy);
        _advertisementAnalysisButton.Enabled = !busy && CanUseAdvertisementAnalysis;
        _settingsButton.Enabled = !busy;
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
