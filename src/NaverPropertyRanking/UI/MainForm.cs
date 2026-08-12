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
    private readonly TextBox _groupId = new() { Width = 210, PlaceholderText = "네이버 부동산 사용자 ID" };
    private readonly TextBox _articleNumbers = new() { Width = 280, PlaceholderText = "예: 2612345678, 2612345679" };
    private readonly CheckBox _saveGroupId = new() { Text = "저장", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Button _loadButton = new() { Text = "내 매물 불러오기", Width = 125, Height = 32 };
    private readonly Button _refreshButton = new() { Text = "랭킹 새로고침", Width = 115, Height = 32 };
    private readonly Button _settingsButton = new() { Text = "설정", Width = 75, Height = 32 };
    private readonly Button _previousPageButton = new() { Text = "◀ 이전", Width = 90, Height = 30 };
    private readonly Button _nextPageButton = new() { Text = "다음 ▶", Width = 90, Height = 30 };
    private readonly Label _pageLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleCenter };
    private readonly ComboBox _pageSizeCombo = new() { Width = 74, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _rankImmediately = new() { Text = "매물 조회 시 랭킹 바로조회", AutoSize = true };
    private readonly Label _authStatusLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView _grid = new();
    private readonly BusyProgressOverlay _busyOverlay;
    private readonly ToolStripStatusLabel _status = new() { Text = "준비" };
    private readonly ToolStripStatusLabel _lastChecked = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _cooldownUiTimer = new() { Interval = 10_000 };
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
    private RankingNotificationForm? _notificationPopup;
    private bool _heartbeatRunning;

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

        Text = authenticationSession is null
            ? "Naver 매물 랭킹 모니터"
            : $"Naver 매물 랭킹 모니터 - {authenticationSession.Name} ({authenticationSession.UserId})";
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
        ConfigureTimer();
        UpdateAuthStatus();
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
        else SetStatus("사용자 ID를 입력한 후 내 매물 불러오기를 눌러 주세요.");
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
            ColumnCount = 6,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = header.BackColor
        };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var title = new Label
        {
            Text = "매물 랭킹 분석 및 알림",
            Dock = DockStyle.Fill,
            Font = new Font("맑은 고딕", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        searchLayout.Controls.Add(title, 0, 0);
        searchLayout.SetColumnSpan(title, 6);

        var groupLabel = new Label
        {
            Text = "사용자 ID",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        _groupId.Dock = DockStyle.Fill;
        _groupId.Margin = new Padding(0, 5, 10, 5);
        _saveGroupId.Dock = DockStyle.Fill;
        _saveGroupId.Margin = new Padding(0, 2, 0, 2);

        foreach (var button in new[] { _loadButton, _settingsButton })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(4, 3, 4, 3);
        }

        searchLayout.Controls.Add(groupLabel, 0, 1);
        searchLayout.Controls.Add(_groupId, 1, 1);
        searchLayout.Controls.Add(_saveGroupId, 2, 1);
        searchLayout.Controls.Add(_authStatusLabel, 3, 1);
        searchLayout.Controls.Add(_loadButton, 4, 1);
        searchLayout.Controls.Add(_settingsButton, 5, 1);
        header.Controls.Add(searchLayout);

        ConfigureGrid();
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var pagingPanel = BuildPagingPanel();
        content.Controls.Add(_grid);
        content.Controls.Add(pagingPanel);
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_lastChecked);

        Controls.Add(content);
        Controls.Add(header);
        Controls.Add(statusStrip);
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
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Address", HeaderText = "주소/매물", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 210 });
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
            Text = "Naver 매물 랭킹 모니터",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private void WireEvents()
    {
        _loadButton.Click += async (_, _) => await StartListingWorkflowAsync();
        _settingsButton.Click += (_, _) => OpenSettings();
        _timer.Tick += async (_, _) =>
        {
            if (_hasLoadedListings) await RefreshSelectedRankingsAsync(true);
        };
        _cooldownUiTimer.Tick += (_, _) => UpdateCooldownUi();
        _grid.CellClick += GridOnCellClick;
        _grid.ColumnHeaderMouseClick += GridOnColumnHeaderMouseClick;
        _grid.CellDoubleClick += GridOnCellDoubleClick;
        _grid.KeyDown += GridOnKeyDown;
        FormClosing += OnFormClosing;
    }

    private async Task StartListingWorkflowAsync()
    {
        if (_refreshing) return;
        _settings.GroupId = _groupId.Text.Trim();
        _settings.SaveGroupId = _saveGroupId.Checked;
        _settings.ManualArticleNumbers = string.Empty;
        if (string.IsNullOrWhiteSpace(_settings.GroupId))
        {
            const string message = "사용자 ID를 입력하세요.";
            SetStatus(message);
            MessageBox.Show(this, message, "입력 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var isInitialLoad = !_hasLoadedListings;
        if (!ShowSettingsDialog()) return;
        if (isInitialLoad)
        {
            _busyOverlay.Hide();
            Hide();
            ShowInTaskbar = false;
            SetStatus("백그라운드에서 매물 목록과 랭킹을 조회합니다.");
        }
        await RefreshAllAsync(false);
        if (isInitialLoad && !_hasLoadedListings) ShowWindow();
    }

    private async Task TrayRefreshAsync()
    {
        if (!_hasLoadedListings)
        {
            ShowWindow();
            SetStatus("사용자 ID 입력 후 내 매물 불러오기를 먼저 실행해 주세요.");
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
            SetStatus("사용자 ID를 입력하세요.");
            if (!isAutomatic) MessageBox.Show(this, _status.Text, "입력 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var authError = GetRequiredAuthError();
        if (authError is not null)
        {
            UpdateAuthStatus();
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
        SetStatus("내 매물 목록을 불러오는 중...");
        try
        {
            _store.SaveSettings(_settings);
            var ownListings = await _apiClient.GetOwnListingsAsync(_settings, _lifetime.Token);
            if (ownListings.Count == 0)
            {
                _ownListings = [];
                _rankingCache.Clear();
                _selectedArticleNumbers.Clear();
                _results = [];
                _currentPage = 1;
                _hasLoadedListings = false;
                RenderGrid();
                SetStatus("조회된 내 매물이 없습니다. 사용자 ID와 인증값을 확인하세요.");
                return;
            }

            _ownListings = ownListings.ToList();
            _rankingCache.Clear();
            _selectedArticleNumbers.Clear();
            _expanded.Clear();
            _currentPage = 1;
            _hasLoadedListings = true;
            UpdatePagingControls();
            await RankCurrentPageCoreAsync(true);
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
            SetBusy(false);
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
            SetStatus("내 매물 불러오기를 먼저 실행해 주세요.");
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
            SetStatus("내 매물 불러오기를 먼저 실행해 주세요.");
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

    private Task RankCurrentPageCoreAsync(bool forceRefresh) =>
        RankListingsCoreAsync(CurrentPageListings(), forceRefresh, $"{_currentPage}/{PageCount}페이지");

    private async Task RankListingsCoreAsync(
        IReadOnlyList<Listing> targetListings,
        bool forceRefresh,
        string scope)
    {
        var ownNumbers = _ownListings.Select(x => x.ArticleNo).ToHashSet();
        var allEvents = new List<NotificationEvent>();
        var attemptedResults = new List<RankingResult>();
        var requestCount = targetListings.Count(listing => forceRefresh || !_rankingCache.ContainsKey(listing.ArticleNo));
        var requestIndex = 0;
        UpdateCurrentPageResults("랭킹 조회 대기");
        RenderGrid();
        ConfigureBusyProgress(requestCount);

        foreach (var listing in targetListings)
        {
            if (!forceRefresh && _rankingCache.ContainsKey(listing.ArticleNo)) continue;
            requestIndex++;
            ReportBusyProgress(requestIndex - 1, requestCount, $"{scope} 랭킹 조회 중\n{requestIndex}/{requestCount} · {listing.ArticleNo}");
            SetStatus($"{scope} 랭킹 조회 중... {requestIndex}/{requestCount} ({listing.ArticleNo})");
            int? previousRank = null;
            if (_rankingCache.TryGetValue(listing.ArticleNo, out var cached) && cached.Success)
                previousRank = cached.Rank;
            else if (_snapshots.TryGetValue(listing.ArticleNo, out var savedSnapshot))
                previousRank = savedSnapshot.Rank;

            var result = (await _apiClient.GetRankingAsync(listing, ownNumbers, _settings, _lifetime.Token)) with
            {
                PreviousRank = previousRank
            };
            attemptedResults.Add(result);
            _rankingCache[listing.ArticleNo] = result;
            ReportBusyProgress(requestIndex, requestCount, $"{scope} 랭킹 조회 중\n{requestIndex}/{requestCount} 완료");
            UpdateCurrentPageResults("랭킹 조회 중");
            RenderGrid();

            if (result.Success)
            {
                _snapshots.TryGetValue(listing.ArticleNo, out var previous);
                var comparison = RankingAnalyzer.Compare(result, previous, _settings);
                _snapshots[listing.ArticleNo] = comparison.Snapshot;
                allEvents.AddRange(comparison.Events);
            }
            if (result.Error?.Contains("429", StringComparison.Ordinal) == true) break;
        }

        UpdateCurrentPageResults("랭킹 미조회");
        _store.SaveSnapshots(_snapshots);
        _store.SaveSettings(_settings);
        RenderGrid();
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
        if (requestCount > 0)
            ShowRankingCompletionPopup(scope, attemptedSuccesses, attemptedFailures, allEvents);

        if (_settings.RateLimitBlockedUntilUtc is { } rateLimitUntil && rateLimitUntil > DateTime.UtcNow)
        {
            HandleRateLimit(rateLimitUntil);
            return;
        }
    }

    private List<Listing> CurrentPageListings() =>
        ListingPagination.GetPage(_ownListings, _currentPage, _settings.DisplayPageSize).ToList();

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

    private int PageCount => ListingPagination.GetPageCount(_ownListings.Count, _settings.DisplayPageSize);

    private void UpdatePagingControls()
    {
        var pageCount = PageCount;
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        var pageSizeText = _settings.DisplayPageSize == 0 ? "전체 표시" : $"페이지당 {_settings.DisplayPageSize}건";
        _pageLabel.Text = $"{_currentPage} / {pageCount} 페이지  ·  전체 {_ownListings.Count}건  ·  {pageSizeText}";
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
        var movement = RankPresentation.GetMovement(result.PreviousRank, result.Rank);
        if (movement != RankMovement.None)
        {
            var movementColor = movement == RankMovement.Up ? Color.Red : Color.Blue;
            row.Cells["CurrentRank"].Style.ForeColor = movementColor;
            row.Cells["CurrentRank"].Style.SelectionForeColor = movementColor;
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
        UpdateAuthStatus();
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

    private void UpdateAuthStatus()
    {
        var realtorError = NaverAuthValidator.GetProfileError(
            "중개인 목록", _apiConfiguration.RealtorArticleList);
        var rankingError = NaverAuthValidator.GetProfileError(
            "랭킹", _apiConfiguration.Ranking);
        var valid = realtorError is null && rankingError is null;
        _authStatusLabel.Text = valid ? "API 인증: 2개 프로필 사용 가능" : "API 인증: appsettings.json 확인 필요";
        _authStatusLabel.ForeColor = valid ? Color.FromArgb(0, 110, 65) : Color.Firebrick;
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
        _refreshButton.Enabled = !_refreshing && !blocked;
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
            _notificationPopup?.Close();
            var popup = new RankingNotificationForm(
                _applicationIcon,
                scope,
                successCount,
                failureCount,
                events,
                ShowWindow);
            _notificationPopup = popup;
            popup.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_notificationPopup, popup)) _notificationPopup = null;
            };
            popup.Show();
            popup.BringToFront();
            popup.Activate();
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
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (_refreshing) _busyOverlay.Show(_status.Text ?? "처리 중...");
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
            _notificationPopup?.Dispose();
            _sessionHeartbeatTimer?.Dispose();
            _timer.Dispose();
            _cooldownUiTimer.Dispose();
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
        _refreshButton.Enabled = !busy && !blocked;
        _settingsButton.Enabled = !busy;
        _groupId.Enabled = !busy;
        _articleNumbers.Enabled = !busy;
        _saveGroupId.Enabled = !busy;
        _pageSizeCombo.Enabled = !busy;
        _rankImmediately.Enabled = !busy;
        _grid.Enabled = !busy;
        ControlBox = !busy;
        UpdatePagingControls();
        UseWaitCursor = false;
        Cursor = Cursors.Default;
        _grid.Cursor = Cursors.Default;

        if (busy)
        {
            _busyOverlay.Show(string.IsNullOrWhiteSpace(_status.Text) ? "처리 중..." : _status.Text);
        }
        else
        {
            _busyOverlay.Hide();
            ActiveControl = null;
        }
    }

    private void ConfigureBusyProgress(int total) => _busyOverlay.ConfigureProgress(total);

    private void ReportBusyProgress(int completed, int total, string message) =>
        _busyOverlay.ReportProgress(completed, total, message);

    private void SetStatus(string text)
    {
        _status.Text = text;
        if (_busyOverlay.IsVisible) _busyOverlay.UpdateMessage(text);
    }

    private async Task SendSessionHeartbeatAsync()
    {
        if (_heartbeatRunning || _authenticationClient is null || _authenticationSession is null) return;
        _heartbeatRunning = true;
        try
        {
            var result = await _authenticationClient.HeartbeatAsync(_authenticationSession, _lifetime.Token);
            if (result.Success) return;
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
