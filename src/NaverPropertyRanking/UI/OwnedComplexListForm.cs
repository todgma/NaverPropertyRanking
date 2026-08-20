using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 광고분석 버튼 클릭 시 표시되는 팝업.
/// 내 매물의 단지번호를 중복 없이 목록으로 보여주고,
/// 단지를 선택하면 단지 정보 API에서 조회한 단지 기본 정보를 아래에 표시한다.
/// </summary>
public sealed class OwnedComplexListForm : Form
{
    private readonly IReadOnlyList<AdvertisementComplex> _complexes;
    private readonly IReadOnlyDictionary<string, ComplexInformation> _information;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ComplexAdvertisementRealtor>> _advertisementRealtors;
    private readonly string _groupId;
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
        IReadOnlyDictionary<string, ComplexInformation>? information = null,
        IReadOnlyDictionary<string, IReadOnlyList<ComplexAdvertisementRealtor>>? advertisementRealtors = null,
        string groupId = "")
    {
        _complexes = complexes;
        _information = information ?? new Dictionary<string, ComplexInformation>(StringComparer.Ordinal);
        _advertisementRealtors = advertisementRealtors ??
                                 new Dictionary<string, IReadOnlyList<ComplexAdvertisementRealtor>>(
                                     StringComparer.Ordinal);
        _groupId = groupId.Trim();
        Text = "광고분석 · 단지 목록";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 520);
        Size = new Size(960, 760);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        ConfigureGrid();
        ConfigureDetailGrid();
        var closeButton = new Button { Text = "닫기", Width = 90, Height = 32 };
        closeButton.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 9, 12, 8),
            BackColor = Color.White
        };
        footer.Controls.Add(closeButton);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BackColor = Color.FromArgb(232, 238, 235)
        };
        split.Panel1.Controls.Add(_grid);
        split.Panel1.BackColor = Color.White;
        split.Panel2.Controls.Add(_detailGrid);
        split.Panel2.BackColor = Color.White;

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(_status);
        Shown += (_, _) =>
        {
            LoadComplexes();
            split.SplitterDistance = Math.Max(140, split.Height / 3);
        };
        _grid.SelectionChanged += (_, _) => ShowSelectedComplexInformation();
    }

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 32;
        _grid.ColumnHeadersHeight = 40;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 249);

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ComplexNo",
            HeaderText = "단지번호",
            Width = 110,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
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
                Width = 185,
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
        _detailGrid.DefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
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
            MinimumWidth = 220,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        });
    }

    private void LoadComplexes()
    {
        _grid.Rows.Clear();
        foreach (var complex in _complexes)
        {
            // 단지 정보 API가 돌려준 공식 단지명을 우선 표시하고, 없으면 매물 기반 단지명을 사용한다.
            var complexName = _information.TryGetValue(complex.ComplexNo, out var info) &&
                              !string.IsNullOrWhiteSpace(info.ComplexName)
                ? info.ComplexName
                : complex.ComplexName;
            var realtors = _advertisementRealtors.TryGetValue(complex.ComplexNo, out var names)
                ? names
                : [];
            var rowIndex = _grid.Rows.Add(
                complex.ComplexNo,
                complexName,
                realtors.ElementAtOrDefault(0)?.RealtorName ?? "-",
                realtors.ElementAtOrDefault(1)?.RealtorName ?? "-",
                realtors.ElementAtOrDefault(2)?.RealtorName ?? "-",
                complex.OwnedListingCount);

            // 광고 중개인이 시스템에 등록된 단체ID(realtorId)와 같으면 내 광고이므로 빨간색 볼드로 강조한다.
            if (_groupId.Length == 0) continue;
            var row = _grid.Rows[rowIndex];
            for (var rank = 0; rank < 3; rank++)
            {
                var realtor = realtors.ElementAtOrDefault(rank);
                if (realtor is null ||
                    !string.Equals(realtor.RealtorId, _groupId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var cell = row.Cells[$"AdvertisedRealtor{rank + 1}"];
                cell.Style.ForeColor = Color.Red;
                cell.Style.SelectionForeColor = Color.Red;
                cell.Style.Font = new Font(Font, FontStyle.Bold);
            }
        }

        _status.Text = $"내 매물 단지 목록 · 단지 {_complexes.Count}곳";
        if (_grid.Rows.Count > 0)
        {
            _grid.Rows[0].Selected = true;
            ShowSelectedComplexInformation();
        }
    }

    private void ShowSelectedComplexInformation()
    {
        _detailGrid.Rows.Clear();
        if (_grid.SelectedRows.Count == 0) return;
        var complexNo = _grid.SelectedRows[0].Cells["ComplexNo"].Value?.ToString() ?? string.Empty;
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
}
