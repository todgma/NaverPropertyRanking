using System.Text.RegularExpressions;
using NaverPropertyRanking.Models;
using NaverPropertyRanking.Services;

namespace NaverPropertyRanking.UI;

public sealed partial class LoginForm : Form
{
    private static readonly Regex UserIdPattern = new("^[A-Za-z0-9._-]{4,50}$", RegexOptions.Compiled);
    private readonly GoogleAuthenticationClient _client;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _loginId = new() { Width = 260 };
    private readonly TextBox _loginPassword = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly TextBox _signUpId = new() { Width = 260 };
    private readonly TextBox _signUpPassword = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly TextBox _signUpName = new() { Width = 260 };
    private readonly Button _loginButton = new() { Text = "로그인", Width = 110, Height = 36 };
    private readonly Button _signUpButton = new() { Text = "회원가입", Width = 110, Height = 36 };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 44,
        Padding = new Padding(18, 6, 18, 6),
        ForeColor = Color.DimGray,
        TextAlign = ContentAlignment.MiddleLeft
    };

    public LoginForm(GoogleAuthenticationClient client, string lastLoginId)
    {
        _client = client;
        Text = "네이버 매물 순위 로그인";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 455);
        Font = new Font("맑은 고딕", 9F);
        BackColor = Color.White;
        _loginId.Text = lastLoginId;

        BuildLayout();
        _tabs.SelectedIndexChanged += (_, _) =>
            AcceptButton = _tabs.SelectedIndex == 0 ? _loginButton : _signUpButton;
        _loginButton.Click += async (_, _) => await LoginAsync();
        _signUpButton.Click += async (_, _) => await SignUpAsync();
        _loginPassword.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await LoginAsync();
        };
        _client.StartWarmUp();
    }

    public AuthenticationSession? Session { get; private set; }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "네이버 매물 순위",
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(18, 16, 18, 0),
            Font = new Font("맑은 고딕", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(3, 105, 65),
            TextAlign = ContentAlignment.TopCenter
        };

        _tabs.TabPages.Add(BuildLoginTab());
        _tabs.TabPages.Add(BuildSignUpTab());
        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(title);
        AcceptButton = _loginButton;
    }

    private TabPage BuildLoginTab()
    {
        var page = new TabPage("로그인") { BackColor = Color.White, Padding = new Padding(34, 24, 34, 18) };
        var layout = BuildFieldLayout();
        AddField(layout, 0, "아이디", _loginId);
        AddField(layout, 1, "패스워드", _loginPassword);
        layout.Controls.Add(new Label
        {
            Text = "로그인 시 현재 PC가 등록되고 로그인 이력이 기록됩니다.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 9, 3, 6)
        }, 1, 2);
        layout.Controls.Add(_loginButton, 1, 3);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSignUpTab()
    {
        var page = new TabPage("회원가입") { BackColor = Color.White, Padding = new Padding(34, 18, 34, 12) };
        var layout = BuildFieldLayout();
        AddField(layout, 0, "아이디", _signUpId);
        AddField(layout, 1, "패스워드", _signUpPassword);
        AddField(layout, 2, "이름", _signUpName);
        layout.Controls.Add(new Label
        {
            Text = "아이디는 영문·숫자·._- 조합 4자 이상, 패스워드는 4자 이상입니다.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 6)
        }, 1, 3);
        layout.Controls.Add(_signUpButton, 1, 4);
        page.Controls.Add(layout);
        return page;
    }

    private static TableLayoutPanel BuildFieldLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(15),
            BackColor = Color.White
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 5; index++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, index < 3 ? 48 : 52));
        return layout;
    }

    private static void AddField(TableLayoutPanel layout, int row, string caption, TextBox textBox)
    {
        layout.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(3, 8, 3, 8);
        layout.Controls.Add(textBox, 1, row);
    }

    private async Task LoginAsync()
    {
        var validation = ValidateCredentials(_loginId.Text, _loginPassword.Text);
        if (validation is not null)
        {
            SetStatus(validation, true);
            return;
        }

        SetBusy(true, "로그인 중...");
        var result = await _client.LoginAsync(_loginId.Text, _loginPassword.Text, CancellationToken.None);
        SetBusy(false, result.Message);
        if (!result.Success || result.Session is null)
        {
            SetStatus(result.Message, true);
            return;
        }

        Session = result.Session;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task SignUpAsync()
    {
        var validation = ValidateCredentials(_signUpId.Text, _signUpPassword.Text);
        if (validation is null && string.IsNullOrWhiteSpace(_signUpName.Text))
            validation = "이름을 입력하세요.";
        if (validation is null && _signUpName.Text.Trim().Length > 50)
            validation = "이름은 50자 이하로 입력하세요.";
        if (validation is not null)
        {
            SetStatus(validation, true);
            return;
        }

        SetBusy(true, "회원가입 처리 중...");
        var result = await _client.SignUpAsync(
            _signUpId.Text,
            _signUpPassword.Text,
            _signUpName.Text,
            CancellationToken.None);
        SetBusy(false, result.Message);
        if (!result.Success)
        {
            SetStatus(result.Message, true);
            return;
        }

        _loginId.Text = _signUpId.Text.Trim();
        _loginPassword.Clear();
        _tabs.SelectedIndex = 0;
        SetStatus("회원가입이 완료되었습니다. 패스워드를 입력해 로그인하세요.", false);
        _loginPassword.Focus();
    }

    private static string? ValidateCredentials(string userId, string password)
    {
        if (!UserIdPattern.IsMatch(userId.Trim()))
            return "아이디는 영문·숫자·._- 조합으로 4~50자를 입력하세요.";
        if (password.Length is < 4 or > 100)
            return "패스워드는 4~100자로 입력하세요.";
        return null;
    }

    private void SetBusy(bool busy, string message)
    {
        _tabs.Enabled = !busy;
        ControlBox = !busy;
        UseWaitCursor = busy;
        SetStatus(message, false);
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }
}
