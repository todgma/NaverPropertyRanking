using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class RankingNotificationForm : Form
{
    private readonly Icon _ownedIcon;
    private readonly int _cascadeIndex;

    public RankingNotificationForm(
        Icon applicationIcon,
        string windowTitle,
        string headline,
        string scope,
        int successCount,
        int failureCount,
        IReadOnlyList<NotificationEvent> events,
        Action openApplication,
        int cascadeIndex = 0)
    {
        _ownedIcon = (Icon)applicationIcon.Clone();
        _cascadeIndex = Math.Max(0, cascadeIndex);
        Icon = _ownedIcon;
        Text = windowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(960, 560);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        var title = new Label
        {
            Text = headline,
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(20, 15, 20, 0),
            Font = new Font("맑은 고딕", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65)
        };
        var summary = new Label
        {
            Text = $"조회 범위: {scope}   ·   성공 {successCount}건   ·   실패 {failureCount}건   ·   변동 {events.Count}건",
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(21, 7, 20, 0),
            ForeColor = failureCount == 0 ? Color.FromArgb(55, 55, 55) : Color.Firebrick
        };
        var details = BuildEventList(events);
        var closeButton = new Button
        {
            Text = "확인",
            Width = 92,
            Height = 34
        };
        var openButton = new Button
        {
            Text = "시스템 열기",
            Width = 112,
            Height = 34
        };
        closeButton.Click += (_, _) => Close();
        openButton.Click += (_, _) =>
        {
            openApplication();
            Close();
        };
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(0, 10, 20, 10),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Controls.Add(openButton);

        var detailPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        detailPanel.Controls.Add(details);
        Controls.Add(detailPanel);
        Controls.Add(buttonPanel);
        Controls.Add(summary);
        Controls.Add(title);
        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    /// <summary>
    /// 목록의 칸 구성. 이름은 매물목록 화면과 같게 맞췄다.
    /// </summary>
    private static readonly (string Name, string Header, int Width)[] ListColumns =
    [
        ("ArticleNo", "매물번호", 110),
        ("ListingName", "매물명", 220),
        ("Dong", "동", 60),
        ("Ho", "호", 60),
        ("TradeSummary", "거래정보", 120),
        ("VerificationType", "검증방식", 90),
        ("Detail", "변동내용", 190)
    ];

    private static Control BuildEventList(IReadOnlyList<NotificationEvent> events)
    {
        if (events.Count == 0)
        {
            return new Label
            {
                Text = "변동 내역이 없습니다.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(248, 250, 249),
                ForeColor = Color.FromArgb(90, 90, 90),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 10F)
            };
        }

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            BackgroundColor = Color.White,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 34,
            // 변동내용이 길면 줄을 넘겨 보여 주고, 그만큼 행 높이를 늘린다.
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("맑은 고딕", 9.5F)
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(33, 46, 42);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        foreach (var (name, header, width) in ListColumns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                // 매물명은 남는 폭을 가져가고 나머지는 고정 폭을 지킨다.
                AutoSizeMode = name == "ListingName"
                    ? DataGridViewAutoSizeColumnMode.Fill
                    : DataGridViewAutoSizeColumnMode.None
            });
        }
        foreach (var centered in new[] { "ArticleNo", "Dong", "Ho", "TradeSummary", "VerificationType" })
        {
            grid.Columns[centered]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }

        foreach (var notificationEvent in events)
        {
            var rowIndex = grid.Rows.Add(
                string.IsNullOrWhiteSpace(notificationEvent.ArticleNo)
                    ? "매물번호 없음"
                    : notificationEvent.ArticleNo,
                string.IsNullOrWhiteSpace(notificationEvent.ListingName)
                    ? notificationEvent.ArticleNo
                    : notificationEvent.ListingName,
                // 동·호는 아직 못 채운 매물이 있어 빈 칸 대신 줄표를 둔다.
                string.IsNullOrWhiteSpace(notificationEvent.Dong) ? "-" : notificationEvent.Dong,
                string.IsNullOrWhiteSpace(notificationEvent.Ho) ? "-" : notificationEvent.Ho,
                string.IsNullOrWhiteSpace(notificationEvent.TradeSummary)
                    ? "거래정보 없음"
                    : notificationEvent.TradeSummary,
                string.IsNullOrWhiteSpace(notificationEvent.VerificationType)
                    ? "-"
                    : notificationEvent.VerificationType,
                $"{notificationEvent.Title}{Environment.NewLine}{notificationEvent.Message}");

            var row = grid.Rows[rowIndex];
            // 변동 종류에 따라 글자색을 달리해 한눈에 구분되게 한다.
            row.Cells["Detail"].Style.ForeColor = HighlightColor(notificationEvent.Highlight);
            row.Cells["Detail"].Style.Font = new Font("맑은 고딕", 10.5F, FontStyle.Bold);
            row.Cells["ArticleNo"].Style.Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold);
            row.Cells["ListingName"].Style.Font = new Font("맑은 고딕", 10F, FontStyle.Bold);
        }

        grid.ClearSelection();
        return grid;
    }

    private static Color HighlightColor(NotificationHighlight highlight) => highlight switch
    {
        NotificationHighlight.RankUp => Color.FromArgb(196, 35, 45),
        NotificationHighlight.RankDown => Color.FromArgb(32, 92, 176),
        NotificationHighlight.Warning => Color.FromArgb(214, 105, 0),
        NotificationHighlight.PriceChange => Color.FromArgb(126, 63, 152),
        NotificationHighlight.NewDuplicate => Color.FromArgb(0, 125, 92),
        _ => Color.FromArgb(55, 55, 55)
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterToScreen();
        var workingArea = Screen.FromControl(this).WorkingArea;
        var offset = _cascadeIndex * 34;
        Location = new Point(
            Math.Clamp(Left + offset, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width)),
            Math.Clamp(Top + offset, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height)));
        // 항상 위에 고정하면 다른 팝업을 계속 덮는다.
        // 뜰 때만 잠깐 위로 올려 앞으로 나오게 하고 곧바로 고정을 푼다.
        BringToFront();
        TopMost = true;
        Activate();
        TopMost = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ownedIcon.Dispose();
        base.Dispose(disposing);
    }
}
