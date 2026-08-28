using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed class PropertyAnalysisForm : Form, IReloadablePopup
{
    private static readonly HashSet<string> IgnoredKeyFields = new(StringComparer.Ordinal)
    {
        "매물번호",
        "공인중개사 ID",
        "정보제공사 ID"
    };

    /// <summary>스크롤해도 최상단에 남기는 행. 순서대로 그리드 맨 위에 고정한다.</summary>
    private static readonly string[] PinnedFields = ["매물번호", "공인중개사명"];

    private Func<CancellationToken, Task<AdvertisementListingAnalysis>>? _refreshAsync;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Dispose가 두 번 불려도 취소 토큰을 다시 건드리지 않게 한다.</summary>
    private bool _lifetimeReleased;
    private AdvertisementListingAnalysis _analysis;
    private readonly Button _refreshButton = new() { Text = "새로고침", Width = 100, Height = 32 };
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

    public PropertyAnalysisForm(
        AdvertisementListingAnalysis analysis,
        Func<CancellationToken, Task<AdvertisementListingAnalysis>>? refreshAsync = null)
    {
        _analysis = analysis;
        _refreshAsync = refreshAsync;
        Text = $"물건분석 · {analysis.RankingResult.OwnListing.ArticleNo}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 520);
        Size = new Size(1380, 760);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;
        ShowInTaskbar = true;

        ConfigureGrid();
        var closeButton = new Button { Text = "닫기", Width = 90, Height = 32 };
        closeButton.Click += (_, _) => Close();
        _refreshButton.Enabled = _refreshAsync is not null;
        _refreshButton.Click += async (_, _) => await RefreshAsync();
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
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(_status);
        FormClosing += (_, _) => CancelLifetime();
        Shown += (_, _) => LoadComparisons();
    }

    /// <summary>
    /// 본 화면의 목록·랭킹이 갱신됐을 때 호출된다.
    /// 비교표는 매물 상세 API 결과로 만들어지는데 그 값은 본 화면이 갖고 있지 않다.
    /// 그래서 여기서는 API를 부르지 않고 순위가 달라졌다는 사실만 알리고,
    /// 실제 비교표 갱신은 사용자가 새로고침을 눌렀을 때 한다.
    /// </summary>
    public void OnListingsUpdated(IReadOnlyDictionary<string, RankingResult> rankingResults)
    {
        if (IsDisposed || _lifetimeReleased) return;
        var articleNo = _analysis.RankingResult.OwnListing.ArticleNo;
        if (!rankingResults.TryGetValue(articleNo, out var updated) || !updated.Success) return;

        var previousRank = _analysis.RankingResult.Rank;
        if (previousRank == updated.Rank &&
            _analysis.RankingResult.Total == updated.Total)
            return;

        _status.Text =
            $"내매물 {articleNo} · 매물목록이 갱신되었습니다" +
            $"(현재순위 {RankText(previousRank)} → {RankText(updated.Rank)}, " +
            $"동일매물 {_analysis.RankingResult.Total}건 → {updated.Total}건). " +
            "새로고침을 누르면 비교표를 다시 조회합니다.";
    }

    private static string RankText(int? rank) => rank is { } value ? $"{value}위" : "-";

    /// <summary>
    /// 창을 새로 만들지 않고 다른 매물의 분석 결과로 내용을 바꾼다.
    /// 팝업은 종류마다 하나만 유지하므로 다른 매물을 분석해도 이 창이 갱신된다.
    /// </summary>
    public void ShowAnalysis(
        AdvertisementListingAnalysis analysis,
        Func<CancellationToken, Task<AdvertisementListingAnalysis>>? refreshAsync)
    {
        _analysis = analysis;
        _refreshAsync = refreshAsync;
        _refreshButton.Enabled = refreshAsync is not null;
        Text = $"물건분석 · {analysis.RankingResult.OwnListing.ArticleNo}";
        LoadComparisons();
    }

    /// <summary>
    /// 창이 닫히는 중이면 취소를 알린다.
    /// Close()와 Dispose에서 모두 불릴 수 있어 이미 정리된 경우에는 아무것도 하지 않는다.
    /// </summary>
    private void CancelLifetime()
    {
        if (_lifetimeReleased) return;
        _lifetime.Cancel();
    }

    /// <summary>동일매물과 상세정보를 다시 조회해 비교표를 갱신한다.</summary>
    private async Task RefreshAsync()
    {
        if (_refreshAsync is null || _lifetimeReleased || IsDisposed) return;

        _refreshButton.Enabled = false;
        var previousStatus = _status.Text;
        _status.Text = $"물건분석 다시 조회 중 · {_analysis.RankingResult.OwnListing.ArticleNo}";
        try
        {
            var refreshed = await _refreshAsync(_lifetime.Token);
            if (IsDisposed) return;
            _analysis = refreshed;
            LoadComparisons();
            _status.Text += $" · {DateTime.Now:HH:mm:ss} 새로고침";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = $"다시 조회 실패: {ex.Message} · {previousStatus}";
        }
        finally
        {
            if (!IsDisposed) _refreshButton.Enabled = true;
        }
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
        // 매물번호·공인중개사명은 어느 매물을 비교 중인지 알려주는 기준이라 맨 위로 올려 고정한다.
        var fieldGroups = comparisons
            .GroupBy(
                item => new { item.Category, item.FieldName },
                item => item)
            .OrderBy(group => PinnedFieldOrder(group.Key.FieldName))
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

        FreezePinnedRows();

        var advertisements = _analysis.TopAdvertisements.OrderBy(item => item.Rank).Take(2).ToList();
        var listing1No = advertisements.ElementAtOrDefault(0)?.Detail.Listing.ArticleNo ?? "없음";
        var listing2No = advertisements.ElementAtOrDefault(1)?.Detail.Listing.ArticleNo ?? "없음";
        _status.Text = $"내매물 {_analysis.RankingResult.OwnListing.ArticleNo} · 매물1 {listing1No} · 매물2 {listing2No}";
    }

    /// <summary>고정 대상은 앞쪽 순번을, 나머지는 뒤 순번을 줘서 원래 순서를 유지한다.</summary>
    private static int PinnedFieldOrder(string fieldName)
    {
        var index = Array.IndexOf(PinnedFields, fieldName);
        return index < 0 ? PinnedFields.Length : index;
    }

    /// <summary>
    /// 맨 위로 올린 행을 스크롤해도 남도록 고정한다.
    /// DataGridView는 지정한 행과 그 위의 모든 행을 함께 고정한다.
    /// </summary>
    private void FreezePinnedRows()
    {
        var pinnedCount = 0;
        for (var index = 0; index < _grid.Rows.Count && index < PinnedFields.Length; index++)
        {
            var fieldName = _grid.Rows[index].Cells["Category"].Value?.ToString();
            if (Array.IndexOf(PinnedFields, fieldName) < 0) break;
            pinnedCount = index + 1;
        }
        if (pinnedCount == 0) return;

        var lastPinned = _grid.Rows[pinnedCount - 1];
        lastPinned.Frozen = true;
        for (var index = 0; index < pinnedCount; index++)
        {
            var row = _grid.Rows[index];
            row.DefaultCellStyle.BackColor = Color.FromArgb(238, 246, 242);
            row.Cells["Category"].Style.BackColor = Color.FromArgb(226, 238, 232);
        }
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

    protected override void Dispose(bool disposing)
    {
        // 모달이 아닌 창은 Close()가 Dispose를 부르고 호출 측에서 또 부를 수 있어 두 번 들어온다.
        if (disposing && !_lifetimeReleased)
        {
            _lifetimeReleased = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
