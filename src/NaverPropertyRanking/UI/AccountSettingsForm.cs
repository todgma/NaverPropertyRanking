using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

/// <summary>
/// 계정설정 팝업. CP 사이트별 아이디·비밀번호를 등록하고 접속 테스트를 실행한다.
/// 저장 위치는 실행 파일 옆이며 비밀번호는 DPAPI로 보호해 기록한다.
/// </summary>
public sealed class AccountSettingsForm : Form
{
    private readonly CpAccountStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _lifetimeReleased;
    private readonly ComboBox _cpCombo = new()
    {
        Width = 220,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _userId = new() { Width = 220 };
    private readonly TextBox _password = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly Button _saveButton = new() { Text = "저장", Width = 90, Height = 30 };
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
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 38,
        Padding = new Padding(12, 0, 12, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(247, 250, 249),
        ForeColor = Color.FromArgb(55, 70, 65)
    };

    public AccountSettingsForm(CpAccountStore store)
    {
        _store = store;
        Text = "계정설정";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 520);
        Size = new Size(820, 580);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;

        ConfigureGrid();
        Controls.Add(BuildListPanel());
        Controls.Add(BuildInputPanel());
        Controls.Add(BuildFooter());
        Controls.Add(_status);

        foreach (var site in CpSite.All) _cpCombo.Items.Add(site);
        if (_cpCombo.Items.Count > 0) _cpCombo.SelectedIndex = 0;
        _saveButton.Click += (_, _) => SaveAccount();
        _cpCombo.SelectedIndexChanged += (_, _) => LoadSelectedCpIntoInputs();
        _grid.CellClick += GridOnCellClick;

        FormClosing += (_, _) =>
        {
            if (!_lifetimeReleased) _lifetime.Cancel();
        };

        LoadAccounts();
        LoadSelectedCpIntoInputs();
        _status.Text = $"저장 위치: {_store.FilePath}";
    }

    private Control BuildInputPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 152,
            ColumnCount = 3,
            RowCount = 4,
            Padding = new Padding(16, 12, 16, 8),
            BackColor = Color.White
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        AddField(layout, 0, "CP", _cpCombo);
        AddField(layout, 1, "아이디", _userId);
        AddField(layout, 2, "패스워드", _password);
        _saveButton.Margin = new Padding(0, 3, 0, 0);
        layout.Controls.Add(_saveButton, 1, 3);
        return layout;
    }

    private static void AddField(TableLayoutPanel layout, int row, string caption, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        control.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(control, 1, row);
    }

    private Control BuildListPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 8) };
        panel.Controls.Add(_grid);
        panel.Controls.Add(new Label
        {
            Text = "저장된 계정",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("맑은 고딕", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65)
        });
        return panel;
    }

    private Control BuildFooter()
    {
        var closeButton = new Button { Text = "닫기", Width = 90, Height = 32 };
        closeButton.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 9, 16, 8),
            BackColor = Color.White
        };
        footer.Controls.Add(closeButton);
        return footer;
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
        _grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 242, 233);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "CpValue",
            HeaderText = "CP코드",
            Width = 70,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "CpName",
            HeaderText = "CP",
            Width = 150,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "UserId",
            HeaderText = "아이디",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 140,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "SavedAt",
            HeaderText = "저장일시",
            Width = 140,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter
            }
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Test",
            HeaderText = "",
            Text = "접속 테스트",
            UseColumnTextForButtonValue = true,
            Width = 100,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Standard
        });
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Delete",
            HeaderText = "",
            Text = "삭제",
            UseColumnTextForButtonValue = true,
            Width = 70,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            FlatStyle = FlatStyle.Standard
        });
    }

    private void LoadAccounts()
    {
        _grid.Rows.Clear();
        foreach (var account in _store.Load().OrderBy(item => item.CpValue, StringComparer.Ordinal))
        {
            var rowIndex = _grid.Rows.Add(
                account.CpValue,
                account.CpName,
                account.UserId,
                account.SavedAt == default ? "-" : account.SavedAt.ToString("yyyy-MM-dd HH:mm"),
                "접속 테스트",
                "삭제");
            _grid.Rows[rowIndex].Tag = account;
        }
    }

    /// <summary>선택한 CP에 저장된 계정이 있으면 입력란에 채워 수정할 수 있게 한다.</summary>
    private void LoadSelectedCpIntoInputs()
    {
        if (_cpCombo.SelectedItem is not CpSite site) return;
        var saved = _store.Load()
            .FirstOrDefault(account => string.Equals(account.CpValue, site.Value, StringComparison.Ordinal));
        _userId.Text = saved?.UserId ?? string.Empty;
        _password.Text = saved?.Password ?? string.Empty;
    }

    private void SaveAccount()
    {
        if (_cpCombo.SelectedItem is not CpSite site)
        {
            _status.Text = "CP를 선택하세요.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_userId.Text))
        {
            _status.Text = "아이디를 입력하세요.";
            _userId.Focus();
            return;
        }
        if (_password.Text.Length == 0)
        {
            _status.Text = "패스워드를 입력하세요.";
            _password.Focus();
            return;
        }

        try
        {
            _store.Save(site.Value, _userId.Text, _password.Text);
            LoadAccounts();
            _status.Text = $"{site.Name} 계정을 저장했습니다 · {_store.FilePath}";
        }
        catch (Exception ex)
        {
            _status.Text = $"저장 실패: {ex.Message}";
            MessageBox.Show(this, ex.Message, "계정설정", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 브라우저 창을 띄우지 않고 내부에서 로그인을 시도한 뒤 접속 가능 여부만 알린다.
    /// 테스트 중에는 목록을 잠가 같은 계정을 연달아 누르지 못하게 한다.
    /// </summary>
    private async Task RunLoginTestAsync(CpAccount account)
    {
        var site = CpSite.Find(account.CpValue);
        if (site is null)
        {
            _status.Text = $"지원하지 않는 CP입니다: {account.CpValue}";
            return;
        }
        if (string.IsNullOrEmpty(account.Password))
        {
            _status.Text = "저장된 비밀번호를 읽지 못했습니다. 다시 저장해 주세요.";
            return;
        }

        _grid.Enabled = false;
        _status.BackColor = Color.FromArgb(247, 250, 249);
        _status.ForeColor = Color.FromArgb(55, 70, 65);
        _status.Text = $"{site.Name} 접속 테스트 중... · {account.UserId}";
        try
        {
            var result = await CpLoginTester.TestAsync(site, account, _lifetime.Token);
            if (IsDisposed) return;

            _status.Text = $"{site.Name} · {account.UserId} · {result.Message}";
            _status.BackColor = result.Success ? Color.FromArgb(226, 245, 234) : Color.FromArgb(253, 234, 234);
            _status.ForeColor = result.Success ? Color.FromArgb(16, 105, 60) : Color.Firebrick;
            MessageBox.Show(
                this,
                result.Success
                    ? $"{site.Name} 접속에 성공했습니다.\n아이디: {account.UserId}"
                    : $"{site.Name} 접속에 실패했습니다.\n아이디: {account.UserId}\n\n{result.Message}",
                "접속 테스트",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!IsDisposed) _grid.Enabled = true;
        }
    }

    private async void GridOnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Rows[e.RowIndex].Tag is not CpAccount account) return;
        var columnName = _grid.Columns[e.ColumnIndex].Name;

        if (columnName == "Test")
        {
            await RunLoginTestAsync(account);
            return;
        }

        if (columnName != "Delete") return;
        var answer = MessageBox.Show(
            this,
            $"{account.CpName} 계정({account.UserId})을 삭제하시겠습니까?",
            "계정설정",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _store.Remove(account.CpValue);
        LoadAccounts();
        LoadSelectedCpIntoInputs();
        _status.Text = $"{account.CpName} 계정을 삭제했습니다.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_lifetimeReleased)
        {
            _lifetimeReleased = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
