using NaverPropertyRanking.Models;

namespace NaverPropertyRanking.UI;

public sealed class SettingsForm : Form
{
    private readonly NumericUpDown _pollMinutes = new() { Minimum = 2, Maximum = 1440, Width = 90 };
    private readonly CheckBox _autoRefresh = new() { Text = "자동 조회 사용", AutoSize = true };
    private readonly CheckBox _rankChange = new() { Text = "모든 랭킹 변경 알림", AutoSize = true };
    private readonly CheckBox _rankThreshold = new() { Text = "랭킹 숫자가 기준 이상(순위 하락)일 때 알림", AutoSize = true };
    private readonly NumericUpDown _threshold = new() { Minimum = 1, Maximum = 9999, Width = 90 };
    private readonly CheckBox _priceChange = new() { Text = "타 중개사 동일매물 가격 변경 알림", AutoSize = true };
    private readonly CheckBox _newDuplicate = new() { Text = "내 단독매물에 동일매물이 생길 때 알림", AutoSize = true };

    public SettingsForm(AppSettings current)
    {
        Text = "조회 및 알림 설정";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 500);
        Size = new Size(760, 540);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        EditedSettings = current.Clone();
        BuildLayout();
        LoadValues(current);
    }

    public AppSettings EditedSettings { get; private set; }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        AddSection(root, ref row, "자동 조회");
        AddRow(root, ref row, "조회 간격(분)", _pollMinutes);
        AddRow(root, ref row, string.Empty, _autoRefresh);

        AddSection(root, ref row, "알림 조건");
        AddRow(root, ref row, string.Empty, _rankChange);
        AddRow(root, ref row, string.Empty, _rankThreshold);
        AddRow(root, ref row, "랭킹 기준", _threshold);
        AddRow(root, ref row, string.Empty, _priceChange);
        AddRow(root, ref row, string.Empty, _newDuplicate);

        var credentialHelp = new Label
        {
            Text = "중개인 목록 API와 랭킹 API의 인증 헤더는 실행 파일 옆 appsettings.json에서 각각 관리합니다.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            MaximumSize = new Size(500, 0)
        };
        AddSection(root, ref row, "API 인증");
        AddRow(root, ref row, string.Empty, credentialHelp, 42);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 0)
        };
        var save = new Button { Text = "확인", DialogResult = DialogResult.None, Width = 90, Height = 34 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 90, Height = 34 };
        save.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(buttons, 0, row);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void LoadValues(AppSettings settings)
    {
        _pollMinutes.Value = Math.Clamp(settings.PollIntervalMinutes, 2, 1440);
        _autoRefresh.Checked = settings.AutoRefresh;
        _rankChange.Checked = settings.NotifyEveryRankChange;
        _rankThreshold.Checked = settings.NotifyRankThreshold;
        _threshold.Value = Math.Clamp(settings.RankThreshold, 1, 9999);
        _priceChange.Checked = settings.NotifyCompetitorPriceChange;
        _newDuplicate.Checked = settings.NotifyNewDuplicate;
    }

    private void SaveAndClose()
    {
        EditedSettings.PollIntervalMinutes = (int)_pollMinutes.Value;
        EditedSettings.AutoRefresh = _autoRefresh.Checked;
        EditedSettings.StartMinimized = false;
        EditedSettings.NotifyEveryRankChange = _rankChange.Checked;
        EditedSettings.NotifyRankThreshold = _rankThreshold.Checked;
        EditedSettings.RankThreshold = (int)_threshold.Value;
        EditedSettings.NotifyCompetitorPriceChange = _priceChange.Checked;
        EditedSettings.NotifyNewDuplicate = _newDuplicate.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddSection(TableLayoutPanel root, ref int row, string title)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("맑은 고딕", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 5)
        };
        root.Controls.Add(label, 0, row);
        root.SetColumnSpan(label, 2);
        row++;
    }

    private static void AddRow(TableLayoutPanel root, ref int row, string caption, Control control, int height = 38)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        if (!string.IsNullOrWhiteSpace(caption))
        {
            root.Controls.Add(new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
        }
        control.Margin = new Padding(3, 5, 3, 5);
        root.Controls.Add(control, 1, row);
        row++;
    }
}
