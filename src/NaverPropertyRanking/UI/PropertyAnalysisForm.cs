using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class PropertyAnalysisForm : Form
{
    private static readonly HashSet<string> IgnoredKeyFields = new(StringComparer.Ordinal)
    {
        "매물번호",
        "공인중개사 ID",
        "정보제공사 ID"
    };

    private readonly AdvertisementListingAnalysis _analysis;
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
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false
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

    public PropertyAnalysisForm(AdvertisementListingAnalysis analysis)
    {
        _analysis = analysis;
        Text = $"물건분석 · {analysis.RankingResult.OwnListing.ArticleNo}";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(980, 520);
        Size = new Size(1380, 760);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        ConfigureGrid();
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
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(_status);
        Shown += (_, _) => LoadComparisons();
    }

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 48;
        _grid.ColumnHeadersHeight = 46;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Category",
            HeaderText = "카테고리",
            Width = 220,
            Frozen = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(245, 247, 246),
                Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 4, 0)
            }
        });
        AddValueColumn("OwnValue", "내매물");
        AddValueColumn("Listing1", "매물1");
        AddValueColumn("Listing2", "매물2");

        var ownColumn = _grid.Columns["OwnValue"];
        ownColumn.DefaultCellStyle.BackColor = Color.FromArgb(255, 249, 204);
        ownColumn.HeaderCell.Style.BackColor = Color.FromArgb(255, 239, 142);
        ownColumn.HeaderCell.Style.ForeColor = Color.FromArgb(70, 58, 0);
    }

    private void AddValueColumn(string name, string headerText)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = headerText,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 230,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void LoadComparisons()
    {
        _grid.Rows.Clear();
        var comparisons = AdvertisementAnalysisService.BuildFieldComparisons(
            [_analysis],
            2);
        var fieldGroups = comparisons
            .GroupBy(
                item => new { item.Category, item.FieldName },
                item => item)
            .ToList();

        foreach (var fieldGroup in fieldGroups)
        {
            var reference = fieldGroup.First();
            var ownValue = DisplayValue(reference.OwnValue);
            var listing1 = fieldGroup.FirstOrDefault(item => item.AdvertisementRank == 1);
            var listing2 = fieldGroup.FirstOrDefault(item => item.AdvertisementRank == 2);
            var listing1Value = DisplayValue(listing1?.AdvertisementValue);
            var listing2Value = DisplayValue(listing2?.AdvertisementValue);
            var rowIndex = _grid.Rows.Add(
                fieldGroup.Key.FieldName,
                ownValue,
                listing1Value,
                listing2Value);
            var row = _grid.Rows[rowIndex];
            row.Cells["OwnValue"].ToolTipText = $"내매물 · {fieldGroup.Key.FieldName}: {ownValue}";
            ConfigureComparisonCell(
                row.Cells["Listing1"],
                fieldGroup.Key.FieldName,
                ownValue,
                listing1Value,
                listing1);
            ConfigureComparisonCell(
                row.Cells["Listing2"],
                fieldGroup.Key.FieldName,
                ownValue,
                listing2Value,
                listing2);
        }

        var advertisements = _analysis.TopAdvertisements.OrderBy(item => item.Rank).Take(2).ToList();
        var listing1No = advertisements.ElementAtOrDefault(0)?.Detail.Listing.ArticleNo ?? "없음";
        var listing2No = advertisements.ElementAtOrDefault(1)?.Detail.Listing.ArticleNo ?? "없음";
        _status.Text = $"내매물 {_analysis.RankingResult.OwnListing.ArticleNo} · 매물1 {listing1No} · 매물2 {listing2No}";
    }

    private static void ConfigureComparisonCell(
        DataGridViewCell cell,
        string fieldName,
        string ownValue,
        string comparisonValue,
        AdvertisementFieldComparison? comparison)
    {
        if (comparison is null)
        {
            cell.ToolTipText = "비교 매물 없음";
            return;
        }

        cell.ToolTipText = $"내매물: {ownValue}\n비교매물: {comparisonValue}";
        if (!IgnoredKeyFields.Contains(fieldName) &&
            !string.Equals(ownValue, comparisonValue, StringComparison.Ordinal))
            cell.Style.ForeColor = Color.Firebrick;
    }

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
