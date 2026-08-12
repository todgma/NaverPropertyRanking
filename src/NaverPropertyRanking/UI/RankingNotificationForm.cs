using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class RankingNotificationForm : Form
{
    private readonly Icon _ownedIcon;

    public RankingNotificationForm(
        Icon applicationIcon,
        string scope,
        int successCount,
        int failureCount,
        IReadOnlyList<NotificationEvent> events,
        Action openApplication)
    {
        _ownedIcon = (Icon)applicationIcon.Clone();
        Icon = _ownedIcon;
        Text = "랭킹 조회 완료 알림";
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
            Text = "랭킹 조회가 완료되었습니다.",
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
        var details = new TextBox
        {
            Text = RankingNotificationFormatter.Format(events),
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(248, 250, 249),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(20, 0, 20, 12),
            Font = new Font("맑은 고딕", 10F)
        };
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterToScreen();
        BringToFront();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ownedIcon.Dispose();
        base.Dispose(disposing);
    }
}
