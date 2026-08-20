using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class AdvertisementAnalysisForm : Form
{
    private readonly IReadOnlyList<AdvertisementListingAnalysis> _analyses;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
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

    public AdvertisementAnalysisForm(IReadOnlyList<AdvertisementListingAnalysis> analyses)
    {
        _analyses = analyses;
        Text = "광고분석 · 동일매물 상위 중개사 3곳";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1050, 430);
        Size = new Size(1420, 600);
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
        Shown += (_, _) => LoadAdvertisements();
    }

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 36;
        _grid.ColumnHeadersHeight = 44;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 249);

        AddColumn("OwnArticleNo", "기준 매물번호", 125, true);
        AddColumn("Rank", "순위", 65, true);
        AddColumn("ExposureRank", "노출순위", 75, true);
        AddColumn("ArticleNo", "동일매물번호", 125, true);
        AddColumn("RealtorName", "공인중개사", 170);
        AddColumn("RealtorId", "중개사 ID", 110);
        AddColumn("Provider", "CP사", 95);
        AddColumn("VerificationMethod", "검증방식", 95);
        AddColumn("ArticleName", "단지/매물명", 210);
        AddColumn("PropertyType", "매물유형", 90);
        AddColumn("TradeType", "거래유형", 80);
        AddColumn("Price", "거래금액", 115);
        AddColumn("RegisteredDate", "등록일", 90);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "설명",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 180,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleLeft
            }
        });
    }

    private void LoadAdvertisements()
    {
        _grid.Rows.Clear();
        var realtorCount = 0;
        foreach (var analysis in _analyses)
        {
            foreach (var advertisement in analysis.TopAdvertisements.OrderBy(item => item.Rank).Take(3))
            {
                var listing = advertisement.Detail.Listing;
                _grid.Rows.Add(
                    analysis.RankingResult.OwnListing.ArticleNo,
                    $"{advertisement.Rank}위",
                    $"{advertisement.ExposureRank}위",
                    listing.ArticleNo,
                    DisplayValue(listing.RealtorName),
                    DisplayValue(listing.RealtorId),
                    DisplayValue(listing.ProviderName),
                    VerificationTypeFormatter.Format(listing.VerificationTypeCode),
                    DisplayValue(FirstNotEmpty(listing.ArticleName, listing.Address)),
                    DisplayValue(listing.RealEstateType),
                    DisplayValue(listing.TradeType),
                    DisplayValue(listing.Price),
                    FormatDate(listing.RegisteredDate),
                    DisplayValue(listing.Description));
                realtorCount++;
            }
        }

        _status.Text = $"광고분석 완료 · 기준 매물 {_analyses.Count}건 · 동일매물 상위 중개사 {realtorCount}곳";
    }

    private void AddColumn(string name, string headerText, int width, bool frozen = false)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = headerText,
            Width = width,
            Frozen = frozen,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
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
