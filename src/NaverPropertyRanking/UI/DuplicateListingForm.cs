using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 매물 목록의 돋보기 버튼으로 여는 동일매물 목록 팝업.
/// 모달이 아니라 일반 창이라 열어 둔 채로 본 화면을 계속 쓸 수 있다.
/// </summary>
public sealed class DuplicateListingForm : Form, IReloadablePopup
{
    private Func<CancellationToken, Task<RankingResult>>? _refreshAsync;
    private readonly string _groupId;
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>Dispose가 두 번 불려도 취소 토큰을 다시 건드리지 않게 한다.</summary>
    private bool _lifetimeReleased;
    private RankingResult _result;
    private Font? _boldFont;
    private readonly Button _refreshButton = new() { Text = "새로고침", Width = 100, Height = 32 };
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

    public DuplicateListingForm(
        RankingResult result,
        string groupId = "",
        Func<CancellationToken, Task<RankingResult>>? refreshAsync = null)
    {
        _result = result;
        _groupId = groupId.Trim();
        _refreshAsync = refreshAsync;
        Text = $"동일매물 목록 · {result.OwnListing.ArticleNo}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 460);
        Size = new Size(1240, 680);
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
        Shown += (_, _) => LoadRows();
        _grid.CellDoubleClick += GridOnCellDoubleClick;
    }

    private void ConfigureGrid()
    {
        _grid.RowTemplate.Height = 30;
        _grid.ColumnHeadersHeight = 40;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(33, 46, 42);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9F, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 249);

        AddColumn("Rank", "노출순위", 80, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("Mine", "구분", 80, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("ArticleNo", "매물번호", 110, DataGridViewContentAlignment.MiddleCenter,
            "더블 클릭하면 네이버 부동산에서 매물을 엽니다.");
        AddColumn("PropertyType", "매물유형", 90, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("ComplexName", "단지명", 150, DataGridViewContentAlignment.MiddleLeft);
        AddColumn("Trade", "거래유형", 80, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("Price", "거래금액", 120, DataGridViewContentAlignment.MiddleLeft);
        AddColumn("RegisteredDate", "등록일", 90, DataGridViewContentAlignment.MiddleCenter);
        AddColumn("Realtor", "공인중개사", 170, DataGridViewContentAlignment.MiddleLeft);
        AddColumn("Provider", "CP사", 100, DataGridViewContentAlignment.MiddleLeft);
        AddColumn("VerificationMethod", "검증방식", 95, DataGridViewContentAlignment.MiddleCenter);

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "설명",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 200,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void AddColumn(
        string name,
        string headerText,
        int width,
        DataGridViewContentAlignment alignment,
        string? toolTip = null)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = headerText,
            ToolTipText = toolTip ?? string.Empty,
            Width = width,
            MinimumWidth = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        });
    }

    private void LoadRows()
    {
        _grid.Rows.Clear();
        var ownArticleNo = _result.OwnListing.ArticleNo;
        for (var index = 0; index < _result.Comparables.Count; index++)
        {
            var listing = _result.Comparables[index];
            var isMine = listing.IsMine ||
                         string.Equals(listing.ArticleNo, ownArticleNo, StringComparison.Ordinal);
            var rowIndex = _grid.Rows.Add(
                $"{index + 1}위",
                isMine ? "내 매물" : "동일매물",
                listing.ArticleNo,
                DisplayOrDash(listing.RealEstateType),
                DisplayOrDash(listing.ArticleName),
                listing.TradeType,
                listing.Price,
                RegistrationDateDisplay(listing.RegisteredDate),
                listing.RealtorName,
                listing.ProviderName,
                VerificationTypeFormatter.Format(listing.VerificationTypeCode),
                listing.Description);
            var row = _grid.Rows[rowIndex];
            if (isMine)
            {
                // 본 화면 매물 목록의 내 매물 행과 같은 연초록 배경·볼드로 통일한다.
                row.DefaultCellStyle.BackColor = Color.FromArgb(244, 250, 247);
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
                row.DefaultCellStyle.Font = BoldFont();
            }
            else if (_groupId.Length > 0 &&
                     string.Equals(listing.RealtorId, _groupId, StringComparison.OrdinalIgnoreCase))
            {
                // 같은 단체의 다른 매물도 구분해 준다.
                row.Cells["Realtor"].Style.ForeColor = Color.Red;
                row.Cells["Realtor"].Style.SelectionForeColor = Color.Red;
            }
        }

        var priceRange = string.IsNullOrWhiteSpace(_result.SameAddressMinPrice)
            ? string.Empty
            : $" · 가격 {_result.SameAddressMinPrice} ~ {_result.SameAddressMaxPrice}";
        _status.Text = $"내 매물 {ownArticleNo} · 현재순위 " +
                       $"{(_result.Rank is { } rank ? rank + "위" : "-")} / 동일매물 {_result.Comparables.Count}건{priceRange}";
    }

    /// <summary>
    /// 본 화면의 목록·랭킹이 갱신됐을 때 호출된다.
    /// 본 화면이 이미 받아 둔 결과를 그대로 쓰므로 API를 다시 부르지 않는다.
    /// </summary>
    public void OnListingsUpdated(IReadOnlyDictionary<string, RankingResult> rankingResults)
    {
        if (IsDisposed || _lifetimeReleased) return;
        if (!rankingResults.TryGetValue(_result.OwnListing.ArticleNo, out var updated)) return;
        if (!updated.Success) return;

        _result = updated;
        LoadRows();
        _status.Text += $" · {DateTime.Now:HH:mm:ss} 목록 갱신 반영";
    }

    /// <summary>
    /// 창을 새로 만들지 않고 다른 매물의 동일매물 목록으로 내용을 바꾼다.
    /// 팝업은 종류마다 하나만 유지하므로 다른 매물의 돋보기를 눌러도 이 창이 갱신된다.
    /// </summary>
    public void ShowListing(
        RankingResult result,
        Func<CancellationToken, Task<RankingResult>>? refreshAsync)
    {
        _result = result;
        _refreshAsync = refreshAsync;
        _refreshButton.Enabled = refreshAsync is not null;
        Text = $"동일매물 목록 · {result.OwnListing.ArticleNo}";
        LoadRows();
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

    /// <summary>동일매물 목록을 다시 조회한다.</summary>
    private async Task RefreshAsync()
    {
        if (_refreshAsync is null || _lifetimeReleased || IsDisposed) return;

        _refreshButton.Enabled = false;
        var previousStatus = _status.Text;
        _status.Text = $"동일매물 다시 조회 중 · {_result.OwnListing.ArticleNo}";
        try
        {
            var refreshed = await _refreshAsync(_lifetime.Token);
            if (IsDisposed) return;
            if (!refreshed.Success)
            {
                _status.Text = $"다시 조회 실패: {refreshed.Error}";
                return;
            }
            _result = refreshed;
            LoadRows();
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

    private void GridOnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != "ArticleNo") return;

        var articleNo = _grid.Rows[e.RowIndex].Cells["ArticleNo"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(articleNo)) return;
        var listing = _result.Comparables
            .FirstOrDefault(item => string.Equals(item.ArticleNo, articleNo, StringComparison.Ordinal));
        if (listing is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = NaverArticleLinkBuilder.Build(listing),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status.Text = $"브라우저를 열 수 없습니다: {ex.Message}";
        }
    }

    private Font BoldFont() => _boldFont ??= new Font(Font, FontStyle.Bold);

    private static string DisplayOrDash(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string RegistrationDateDisplay(string registeredDate)
    {
        var value = (registeredDate ?? string.Empty).Trim();
        if (value.Length != 8) return value;
        return $"{value[2..4]}.{value[4..6]}.{value[6..8]}";
    }

    protected override void Dispose(bool disposing)
    {
        // 모달이 아닌 창은 Close()가 Dispose를 부르고 호출 측에서 또 부를 수 있어 두 번 들어온다.
        if (disposing && !_lifetimeReleased)
        {
            _lifetimeReleased = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
