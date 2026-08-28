using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 금액변동확인 버튼을 눌렀을 때 표시하는 팝업.
/// 금액이 바뀐 동일매물의 매물번호·중개업소명·이전/변동 금액·등록일·검증방식을 보여준다.
/// </summary>
public sealed class PriceChangeDetailForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = false,
        AllowUserToResizeColumns = true,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };

    public PriceChangeDetailForm(Listing ownListing, IReadOnlyList<PriceChangeDetail> changes)
    {
        Text = "금액변동 확인";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 340);
        Size = new Size(900, 460);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(14, 0, 14, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(247, 250, 249),
            ForeColor = Color.FromArgb(55, 70, 65),
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            Text = $"내 매물 {ownListing.ArticleNo} · 동일매물 금액변동 {changes.Count}건"
        };

        ConfigureGrid();
        foreach (var change in changes)
        {
            var rowIndex = _grid.Rows.Add(
                change.ArticleNo,
                DisplayValue(change.RealtorName),
                DisplayValue(change.PreviousPrice),
                DisplayValue(change.CurrentPrice),
                DisplayValue(RegistrationDateDisplay(change.RegisteredDate)),
                DisplayValue(change.VerificationType));

            // 금액이 올랐으면 빨간색, 내렸으면 파란색으로 표시한다.
            var direction = PriceComparer.Compare(change.PreviousPrice, change.CurrentPrice);
            if (direction == 0) continue;
            var color = direction > 0 ? Color.Red : Color.Blue;
            var cell = _grid.Rows[rowIndex].Cells["CurrentPrice"];
            cell.Style.ForeColor = color;
            cell.Style.SelectionForeColor = color;
        }

        var confirmButton = new Button
        {
            Text = "확인",
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.OK
        };
        confirmButton.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 10, 14, 8),
            BackColor = Color.White
        };
        footer.Controls.Add(confirmButton);

        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = confirmButton;
    }

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 32;
        _grid.ColumnHeadersHeight = 38;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 249);

        AddColumn("ArticleNo", "네이버매물번호", 130, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("RealtorName", "중개업소명", 180, DataGridViewContentAlignment.MiddleLeft);
        AddColumn("PreviousPrice", "이전금액", 130, DataGridViewContentAlignment.MiddleRight);
        AddColumn("CurrentPrice", "변동된 금액", 130, DataGridViewContentAlignment.MiddleRight);
        AddColumn("RegisteredDate", "등록일", 100, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("VerificationType", "검증방식", 110, DataGridViewContentAlignment.MiddleCenter);

        _grid.Columns["CurrentPrice"].DefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
    }

    private void AddColumn(string name, string header, int width, DataGridViewContentAlignment alignment) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        });

    private static string DisplayValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

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
}
