using System.Diagnostics;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 광고분석 버튼 클릭 시 표시되는 팝업.
/// 내 매물의 단지번호를 중복 없이 목록으로 먼저 띄운 뒤, 팝업 중앙 진행 표시와 함께
/// 단지별 광고 순위(단지 광고 API)를 순서대로 조회해 목록을 채운다.
/// 단지 상세정보는 별개의 단지 정보 API라서 행을 선택할 때 그 단지만 조용히 조회하고 캐시한다.
/// </summary>
public sealed class OwnedComplexListForm : Form
{
    private IReadOnlyList<AdvertisementComplex> _complexes;
    private readonly Dictionary<string, ComplexInformation> _information =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ComplexAdvertisementRealtor>> _advertisementRealtors =
        new(StringComparer.Ordinal);
    private string _groupId;
    /// <summary>단지 한 곳의 광고 순위를 조회하는 콜백. 팝업이 열릴 때 단지 수만큼 호출한다.</summary>
    private readonly Func<AdvertisementComplex, CancellationToken, Task<IReadOnlyList<ComplexAdvertisementRealtor>>>?
        _loadRealtorsAsync;
    /// <summary>단지 한 곳의 상세정보를 조회하는 콜백. 행을 선택할 때마다 조용히 호출한다.</summary>
    private readonly Func<AdvertisementComplex, CancellationToken, Task<ComplexInformation>>? _loadInformationAsync;
    /// <summary>팝업이 닫히면 진행 중인 조회를 멈춘다.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Dispose가 두 번 불려도 취소 토큰을 다시 건드리지 않게 한다.</summary>
    private bool _lifetimeReleased;
    /// <summary>목록을 다시 채우는 동안 선택 변경 이벤트로 상세 표시가 흔들리지 않게 막는 플래그.</summary>
    private bool _loadingRows;
    /// <summary>가장 최근에 선택된 단지번호. 늦게 도착한 상세정보 응답을 버리는 기준으로 쓴다.</summary>
    private string _pendingComplexNo = string.Empty;
    /// <summary>상세정보를 조회 중인 단지번호. 같은 단지를 연속 선택해도 한 번만 호출한다.</summary>
    private readonly HashSet<string> _inFlightComplexNumbers = new(StringComparer.Ordinal);
    /// <summary>내 광고 강조용 볼드 글꼴. 셀마다 새로 만들면 GDI 핸들이 쌓인다.</summary>
    private Font? _gridBoldFont;
    private readonly Button _refreshButton = new() { Text = "새로고침", Width = 100, Height = 32 };
    /// <summary>단지정보 조회 중 팝업 중앙에 띄우는 진행 표시.</summary>
    private readonly Panel _loadingOverlay = new()
    {
        Visible = false,
        Size = new Size(430, 118),
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _loadingMessage = new()
    {
        Dock = DockStyle.Top,
        Height = 62,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("맑은 고딕", 10F, FontStyle.Bold),
        ForeColor = Color.FromArgb(33, 46, 42),
        Padding = new Padding(14, 10, 14, 0)
    };
    private readonly ProgressBar _loadingProgress = new()
    {
        Style = ProgressBarStyle.Continuous,
        Width = 380,
        Height = 22,
        Location = new Point(24, 72)
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly DataGridView _detailGrid = new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        ColumnHeadersVisible = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Top,
        Height = 42,
        Padding = new Padding(12, 0, 12, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(247, 250, 249),
        ForeColor = Color.FromArgb(55, 70, 65)
    };

    public OwnedComplexListForm(
        IReadOnlyList<AdvertisementComplex> complexes,
        string groupId = "",
        Func<AdvertisementComplex, CancellationToken, Task<IReadOnlyList<ComplexAdvertisementRealtor>>>?
            loadRealtorsAsync = null,
        Func<AdvertisementComplex, CancellationToken, Task<ComplexInformation>>? loadInformationAsync = null)
    {
        _loadRealtorsAsync = loadRealtorsAsync;
        _loadInformationAsync = loadInformationAsync;
        _complexes = complexes;
        _groupId = groupId.Trim();
        Text = "광고분석 · 단지 목록";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 540);
        Size = new Size(1320, 760);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        ConfigureGrid();
        ConfigureDetailGrid();
        var closeButton = new Button { Text = "닫기", Width = 90, Height = 32 };
        closeButton.Click += (_, _) => Close();
        _refreshButton.Enabled = _loadRealtorsAsync is not null;
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _loadingOverlay.Controls.Add(_loadingProgress);
        _loadingOverlay.Controls.Add(_loadingMessage);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 9, 12, 8),
            BackColor = Color.White
        };
        footer.Controls.Add(closeButton);
        footer.Controls.Add(_refreshButton);

        // 왼쪽은 단지별 광고 랭킹 목록, 오른쪽은 선택한 단지의 상세정보를 나란히 본다.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(232, 238, 235)
        };
        split.Panel1.Controls.Add(_grid);
        split.Panel1.Controls.Add(BuildPaneHeader("[랭킹정보]"));
        split.Panel1.BackColor = Color.White;
        split.Panel1.Padding = PanePadding;
        split.Panel2.Controls.Add(_detailGrid);
        split.Panel2.Controls.Add(BuildPaneHeader("[단지상세정보]"));
        split.Panel2.BackColor = Color.White;
        split.Panel2.Padding = PanePadding;

        Controls.Add(_loadingOverlay);
        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(_status);
        Shown += async (_, _) =>
        {
            LoadComplexes();
            split.SplitterDistance = Math.Clamp(RankPaneWidth(), 420, Math.Max(420, split.Width - 320));
            await LoadAdvertisementRanksAsync();
        };
        Resize += (_, _) => CenterLoadingOverlay();
        FormClosing += (_, _) =>
        {
            if (!_lifetimeReleased) _lifetime.Cancel();
        };
        _grid.SelectionChanged += async (_, _) => await ShowSelectedComplexAsync();
        _grid.CellDoubleClick += GridOnCellDoubleClick;
    }

    /// <summary>
    /// 창을 새로 만들지 않고 새 단지 목록으로 내용을 바꿔 처음부터 다시 조회한다.
    /// 팝업은 종류마다 하나만 유지하므로 광고분석을 다시 눌러도 이 창이 갱신된다.
    /// </summary>
    public async void ShowComplexes(IReadOnlyList<AdvertisementComplex> complexes, string groupId)
    {
        _complexes = complexes;
        _groupId = groupId.Trim();
        _information.Clear();
        _advertisementRealtors.Clear();
        LoadComplexes();
        await LoadAdvertisementRanksAsync();
    }

    /// <summary>단지번호를 더블 클릭하면 네이버 부동산 단지 페이지를 브라우저로 연다.</summary>
    private void GridOnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "ComplexNo") return;

        var complexNo = _grid.Rows[e.RowIndex].Cells["ComplexNo"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(complexNo)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NaverArticleLinkBuilder.BuildComplexLink(complexNo),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = $"브라우저를 열 수 없습니다: {ex.Message}";
        }
    }

    /// <summary>좌우 패널 안쪽 여백. 표가 창 경계에 붙지 않게 한다.</summary>
    private static Padding PanePadding => new(10, 0, 10, 8);

    /// <summary>
    /// 랭킹정보 표가 가로 스크롤 없이 다 보이는 폭.
    /// 컬럼 폭 합계에 세로 스크롤바와 패널 여백을 더해 계산한다.
    /// </summary>
    private int RankPaneWidth() =>
        _grid.Columns.Cast<DataGridViewColumn>().Sum(column => column.Width) +
        SystemInformation.VerticalScrollBarWidth +
        PanePadding.Horizontal +
        4;

    /// <summary>좌우 패널 위에 붙이는 제목 줄.</summary>
    private static Label BuildPaneHeader(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(2, 0, 0, 0),
        Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
        ForeColor = Color.FromArgb(3, 105, 65),
        BackColor = Color.FromArgb(247, 250, 249)
    };

    /// <summary>진행 표시를 팝업 정중앙에 놓는다.</summary>
    private void CenterLoadingOverlay() =>
        _loadingOverlay.Location = new Point(
            Math.Max(0, (ClientSize.Width - _loadingOverlay.Width) / 2),
            Math.Max(0, (ClientSize.Height - _loadingOverlay.Height) / 2));

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 32;
        _grid.ColumnHeadersHeight = 40;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.Padding = new Padding(7, 0, 7, 0);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 249);

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ComplexNo",
            HeaderText = "단지번호",
            ToolTipText = "더블 클릭하면 네이버 부동산 단지 페이지를 엽니다.",
            Width = 110,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(16, 90, 170)
            }
        });
        // 단지명은 기존 채움 폭의 절반 수준으로 줄이고, 광고 순위 컬럼을 넓게 잡는다.
        // 모든 컬럼은 사용자가 마우스로 폭을 조절할 수 있다.
        _grid.AllowUserToResizeColumns = true;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ComplexName",
            HeaderText = "단지명",
            Width = 150,
            MinimumWidth = 80,
            Resizable = DataGridViewTriState.True,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        });
        for (var rank = 1; rank <= 3; rank++)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"AdvertisedRealtor{rank}",
                HeaderText = $"광고{rank}순위",
                Width = 150,
                MinimumWidth = 80,
                Resizable = DataGridViewTriState.True,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleLeft
                }
            });
        }
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "OwnedListingCount",
            HeaderText = "내 매물 수",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
    }

    private void ConfigureDetailGrid()
    {
        _detailGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(240, 247, 244);
        _detailGrid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _detailGrid.DefaultCellStyle.Padding = new Padding(9, 6, 9, 6);
        _detailGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _detailGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Field",
            Width = 130,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(247, 250, 249),
                Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        });
        _detailGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 160,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        });
    }

    /// <summary>광고 순위와 이미 조회해 둔 단지 상세정보를 모두 버리고 처음부터 다시 조회한다.</summary>
    private async Task RefreshAsync()
    {
        var selectedComplexNo = _grid.SelectedRows.Count > 0
            ? _grid.SelectedRows[0].Cells["ComplexNo"].Value?.ToString()
            : null;
        _information.Clear();
        _advertisementRealtors.Clear();
        LoadComplexes();
        await LoadAdvertisementRanksAsync(selectedComplexNo);
    }

    /// <summary>
    /// 단지별 광고 순위를 순서대로 조회한다. 목록의 광고 순위 컬럼을 한눈에 비교해야 하므로
    /// 팝업이 열릴 때 전부 조회하고, 진행 상황은 팝업 중앙 진행 표시로 보여준다.
    /// 한 단지가 끝날 때마다 해당 행을 바로 채워 어디까지 왔는지 목록에서도 보이게 한다.
    /// 단지 상세정보는 다른 API라서 여기서 조회하지 않고 행을 선택할 때 가져온다.
    /// </summary>
    private async Task LoadAdvertisementRanksAsync(string? selectComplexNo = null)
    {
        if (_loadRealtorsAsync is null || _complexes.Count == 0)
        {
            await SelectComplexRowAsync(selectComplexNo);
            return;
        }

        // 팝업이 닫히면서 _lifetime이 정리될 수 있으므로 토큰을 미리 잡아 둔다.
        var cancellationToken = _lifetime.Token;
        _refreshButton.Enabled = false;
        ShowLoadingOverlay(_complexes.Count);
        try
        {
            for (var index = 0; index < _complexes.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var complex = _complexes[index];
                ReportLoadingProgress(index, complex);
                try
                {
                    _advertisementRealtors[complex.ComplexNo] =
                        await _loadRealtorsAsync(complex, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // 한 단지가 실패해도 나머지 단지는 계속 조회한다.
                    _advertisementRealtors[complex.ComplexNo] = [];
                }

                if (IsDisposed) return;
                UpdateComplexRow(index, complex);
                ReportLoadingProgress(index + 1, complex);
            }

            _status.Text = $"내 매물 단지 목록 · 단지 {_complexes.Count}곳 · {DateTime.Now:HH:mm:ss} 광고순위 조회";
        }
        finally
        {
            if (!IsDisposed)
            {
                HideLoadingOverlay();
                _refreshButton.Enabled = true;
            }
        }

        await SelectComplexRowAsync(selectComplexNo);
    }

    private void ShowLoadingOverlay(int total)
    {
        _loadingProgress.Minimum = 0;
        _loadingProgress.Maximum = Math.Max(1, total);
        _loadingProgress.Value = 0;
        _loadingMessage.Text = $"광고순위 조회 중 · 0/{total}곳";
        CenterLoadingOverlay();
        _loadingOverlay.Visible = true;
        _loadingOverlay.BringToFront();
    }

    private void ReportLoadingProgress(int completed, AdvertisementComplex complex)
    {
        if (IsDisposed || !_loadingOverlay.Visible) return;
        _loadingProgress.Value = Math.Clamp(completed, _loadingProgress.Minimum, _loadingProgress.Maximum);
        _loadingMessage.Text = $"광고순위 조회 중 · {completed}/{_complexes.Count}곳\n{ComplexDisplayName(complex)}";
    }

    private void HideLoadingOverlay() => _loadingOverlay.Visible = false;

    /// <summary>
    /// 지정한 단지를 선택하고 상세정보를 조회한다. 지정이 없으면 첫 행을 선택한다.
    /// 행 추가 과정에서 그리드가 이미 같은 행을 선택해 두면 SelectionChanged가 뜨지 않으므로,
    /// 이벤트에 기대지 않고 조회를 직접 호출한다. 중복 호출은 조회 측에서 걸러진다.
    /// </summary>
    private async Task SelectComplexRowAsync(string? complexNo)
    {
        if (IsDisposed || _grid.Rows.Count == 0) return;
        var target = _grid.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(row => string.Equals(
                row.Cells["ComplexNo"].Value?.ToString(),
                complexNo,
                StringComparison.Ordinal)) ?? _grid.Rows[0];

        _grid.ClearSelection();
        target.Selected = true;
        _grid.CurrentCell = target.Cells["ComplexName"];
        await ShowSelectedComplexAsync();
    }

    /// <summary>
    /// 단지 목록의 뼈대를 먼저 그린다. 단지번호·단지명·내 매물 수는 내 매물에서 이미 알고 있어
    /// API 호출 없이 바로 표시되고, 광고 순위는 조회가 진행되면서 행마다 채워진다.
    /// </summary>
    private void LoadComplexes()
    {
        _loadingRows = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var complex in _complexes)
            {
                var rowIndex = _grid.Rows.Add(
                    complex.ComplexNo,
                    ComplexDisplayName(complex),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    complex.OwnedListingCount);
                ApplyRealtorCells(_grid.Rows[rowIndex], complex.ComplexNo);
            }
        }
        finally
        {
            _loadingRows = false;
        }

        _detailGrid.Rows.Clear();
        _status.Text = $"내 매물 단지 목록 · 단지 {_complexes.Count}곳";
    }

    /// <summary>조회가 끝난 단지 한 곳의 행을 갱신한다.</summary>
    private void UpdateComplexRow(int index, AdvertisementComplex complex)
    {
        if (index < 0 || index >= _grid.Rows.Count) return;
        var row = _grid.Rows[index];
        row.Cells["ComplexName"].Value = ComplexDisplayName(complex);
        ApplyRealtorCells(row, complex.ComplexNo);
    }

    /// <summary>단지 정보 API가 돌려준 공식 단지명을 우선 쓰고, 없으면 매물 기반 단지명을 쓴다.</summary>
    private string ComplexDisplayName(AdvertisementComplex complex) =>
        _information.TryGetValue(complex.ComplexNo, out var info) &&
        !string.IsNullOrWhiteSpace(info.ComplexName)
            ? info.ComplexName
            : complex.ComplexName;

    /// <summary>광고 1~3순위 셀을 채운다. 아직 조회 전이면 빈 칸으로 둔다.</summary>
    private void ApplyRealtorCells(DataGridViewRow row, string complexNo)
    {
        var loaded = _advertisementRealtors.TryGetValue(complexNo, out var realtors);
        for (var rank = 0; rank < 3; rank++)
        {
            var realtor = loaded ? realtors!.ElementAtOrDefault(rank) : null;
            var cell = row.Cells[$"AdvertisedRealtor{rank + 1}"];
            cell.Value = loaded ? realtor?.RealtorName ?? "-" : string.Empty;

            // 광고 중개인이 시스템에 등록된 단체ID(realtorId)와 같으면 내 광고이므로 빨간색 볼드로 강조한다.
            var isMine = _groupId.Length > 0 &&
                         realtor is not null &&
                         string.Equals(realtor.RealtorId, _groupId, StringComparison.OrdinalIgnoreCase);
            cell.Style.ForeColor = isMine ? Color.Red : _grid.DefaultCellStyle.ForeColor;
            cell.Style.SelectionForeColor = isMine ? Color.Red : _grid.DefaultCellStyle.SelectionForeColor;
            cell.Style.Font = isMine ? GridBoldFont() : null;
        }
    }

    private Font GridBoldFont() => _gridBoldFont ??= new Font(Font, FontStyle.Bold);

    /// <summary>
    /// 선택된 단지의 상세정보를 단지 정보 API로 조회해 아래 표에 표시한다.
    /// 진행 상태는 표시하지 않으며, 한 번 조회한 단지는 캐시해 두고 다시 선택하면 즉시 그린다.
    /// </summary>
    private async Task ShowSelectedComplexAsync()
    {
        if (_loadingRows || _grid.SelectedRows.Count == 0) return;
        var row = _grid.SelectedRows[0];
        var complexNo = row.Cells["ComplexNo"].Value?.ToString() ?? string.Empty;
        _pendingComplexNo = complexNo;

        if (_information.ContainsKey(complexNo) || _loadInformationAsync is null)
        {
            ShowComplexInformation(complexNo);
            return;
        }

        var complex = _complexes.FirstOrDefault(item =>
            string.Equals(item.ComplexNo, complexNo, StringComparison.Ordinal));
        if (complex is null)
        {
            ShowComplexInformation(complexNo);
            return;
        }

        // 같은 단지가 이미 조회 중이면 중복 호출하지 않는다. 진행 중인 호출이 화면을 채워 준다.
        if (!_inFlightComplexNumbers.Add(complexNo)) return;

        var cancellationToken = _lifetime.Token;
        _detailGrid.Rows.Clear();
        try
        {
            _information[complexNo] = await _loadInformationAsync(complex, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _information[complexNo] =
                new ComplexInformation(complexNo, complex.ComplexName) { Error = ex.Message };
        }
        finally
        {
            _inFlightComplexNumbers.Remove(complexNo);
        }

        // 조회 중에 다른 행으로 옮겨갔으면 이 응답은 화면에 반영하지 않는다.
        // 결과는 캐시에 남으므로 그 행으로 돌아오면 바로 보인다.
        if (IsDisposed || !string.Equals(_pendingComplexNo, complexNo, StringComparison.Ordinal)) return;

        // 단지 정보 API가 돌려준 공식 단지명으로 목록의 단지명을 맞춰 준다.
        row.Cells["ComplexName"].Value = ComplexDisplayName(complex);
        ShowComplexInformation(complexNo);
    }

    private void ShowComplexInformation(string complexNo)
    {
        _detailGrid.Rows.Clear();
        if (!_information.TryGetValue(complexNo, out var info))
        {
            _detailGrid.Rows.Add("단지 정보", "조회된 단지 정보가 없습니다.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(info.Error))
        {
            _detailGrid.Rows.Add("조회 실패", info.Error);
            return;
        }

        AddDetailRow("단지명", info.ComplexName);
        AddDetailRow("세대수", info.HouseholdSummary);
        AddDetailRow("저/최고층", info.FloorRange);
        AddDetailRow("사용승인일", info.UseApproveDate);
        AddDetailRow("총주차대수", info.ParkingSummary);
        AddDetailRow("용적률", info.FloorAreaRatio);
        AddDetailRow("건폐율", info.BuildingCoverageRatio);
        AddDetailRow("건설사", info.ConstructionCompany);
        AddDetailRow("난방", info.Heating);
        AddDetailRow("관리사무소", info.ManagementOfficeTel);
        AddDetailRow("주소", info.Address);
        AddDetailRow("도로명", info.RoadAddress);
        AddDetailRow("면적", info.AreaNames);
    }

    private void AddDetailRow(string field, string value) =>
        _detailGrid.Rows.Add(field, string.IsNullOrWhiteSpace(value) ? "정보 없음" : value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeReleased)
        {
            _lifetimeReleased = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _gridBoldFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
