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
        TopMost = true;
        ClientSize = new Size(650, 470);
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

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(242, 245, 244),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8)
        };
        foreach (var notificationEvent in events)
            list.Controls.Add(BuildEventCard(notificationEvent));

        void ResizeCards()
        {
            var width = Math.Max(300, list.ClientSize.Width - list.Padding.Horizontal - 4);
            foreach (Control control in list.Controls) control.Width = width;
        }

        list.ClientSizeChanged += (_, _) => ResizeCards();
        ResizeCards();
        return list;
    }

    private static Control BuildEventCard(NotificationEvent notificationEvent)
    {
        var card = new TableLayoutPanel
        {
            Height = 78,
            Width = 570,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(8),
            BackColor = Color.White,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var listingName = string.IsNullOrWhiteSpace(notificationEvent.ListingName)
            ? notificationEvent.ArticleNo
            : notificationEvent.ListingName;
        var articleNumber = string.IsNullOrWhiteSpace(notificationEvent.ArticleNo)
            ? string.Empty
            : $"\r\n매물번호 {notificationEvent.ArticleNo}";
        card.Controls.Add(new Label
        {
            Text = listingName + articleNumber,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(7, 3, 5, 3),
            Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40)
        }, 0, 0);
        card.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(notificationEvent.TradeSummary)
                ? "거래정보 없음"
                : notificationEvent.TradeSummary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Padding = new Padding(5),
            ForeColor = Color.FromArgb(65, 65, 65)
        }, 1, 0);
        card.Controls.Add(new Label
        {
            Text = $"{notificationEvent.Title}\r\n{notificationEvent.Message}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(7, 3, 5, 3),
            Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold),
            ForeColor = HighlightColor(notificationEvent.Highlight)
        }, 2, 0);
        return card;
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
        BringToFront();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ownedIcon.Dispose();
        base.Dispose(disposing);
    }
}
