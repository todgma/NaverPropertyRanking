namespace NaverPropertyRanking.UI;

/// <summary>
/// 매물목록에 어떤 항목을 보여줄지 고르는 창.
/// 항목마다 한 줄씩 보여 주고 체크한 것만 목록에 남긴다.
/// </summary>
public sealed class ColumnVisibilityForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        EnableHeadersVisualStyles = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 34,
        RowTemplate = { Height = 30 }
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 26,
        Padding = new Padding(4, 4, 4, 0),
        ForeColor = Color.FromArgb(90, 90, 90)
    };

    private readonly Button _moveUp = new() { Text = "▲ 위로", Width = 90, Height = 30 };
    private readonly Button _moveDown = new() { Text = "▼ 아래로", Width = 90, Height = 30 };

    /// <summary>확인을 누르면 채워지는 결과. 항목 이름별 표시 여부다.</summary>
    public Dictionary<string, bool> Result { get; } = new(StringComparer.Ordinal);

    /// <summary>확인을 누르면 채워지는 표시 순서. 창에 보이는 위에서 아래 순서 그대로다.</summary>
    public List<string> Order { get; } = [];

    public ColumnVisibilityForm(IReadOnlyList<(string Name, string Header, bool Visible)> columns)
    {
        Text = "항목설정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(430, 520);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;
        Padding = new Padding(12, 12, 12, 12);

        BuildGrid();
        foreach (var (name, header, visible) in columns)
        {
            var rowIndex = _grid.Rows.Add(header, visible);
            _grid.Rows[rowIndex].Tag = name;
        }

        var okButton = new Button { Text = "확인", Width = 90, Height = 32, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "취소", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        okButton.Click += (_, _) => Apply();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        // 순서 이동 버튼은 목록 오른쪽에 세로로 둔다.
        var sideButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 104,
            Padding = new Padding(8, 0, 0, 0),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        _moveUp.Click += (_, _) => MoveSelectedRow(-1);
        _moveDown.Click += (_, _) => MoveSelectedRow(1);
        sideButtons.Controls.Add(_moveUp);
        sideButtons.Controls.Add(_moveDown);

        Controls.Add(_grid);
        Controls.Add(sideButtons);
        Controls.Add(_status);
        Controls.Add(buttons);
        AcceptButton = okButton;
        CancelButton = cancelButton;
        _status.Text = "체크한 항목만 표시되고, 위에서 아래 순서대로 놓입니다.";
    }

    private void BuildGrid()
    {
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(33, 46, 42);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Header",
            HeaderText = "필드명",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Padding = new Padding(6, 0, 6, 0) }
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Visible",
            HeaderText = "표시여부",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        // 체크는 클릭 즉시 반영되게 한다. 그러지 않으면 확인을 눌러도 직전 클릭이 빠진다.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    /// <summary>고른 줄을 위나 아래로 한 칸 옮긴다.</summary>
    private void MoveSelectedRow(int step)
    {
        var current = _grid.CurrentRow;
        if (current is null) return;

        var target = current.Index + step;
        if (target < 0 || target >= _grid.Rows.Count) return;

        // 행을 통째로 빼서 옮기고, 옮긴 줄을 그대로 선택해 둔다.
        _grid.Rows.Remove(current);
        _grid.Rows.Insert(target, current);
        _grid.ClearSelection();
        current.Selected = true;
        _grid.CurrentCell = current.Cells["Header"];
    }

    private void Apply()
    {
        Result.Clear();
        Order.Clear();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not string name) continue;
            Result[name] = row.Cells["Visible"].Value is true;
            Order.Add(name);
        }

        // 모두 끄면 빈 목록이 되어 되돌리기 어렵다. 최소 한 개는 남긴다.
        if (Result.Count > 0 && !Result.Values.Any(visible => visible))
        {
            var first = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Tag is string);
            if (first?.Tag is string name) Result[name] = true;
        }
    }
}
