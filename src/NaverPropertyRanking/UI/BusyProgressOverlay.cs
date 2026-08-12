namespace NaverPropertyRanking.UI;

public sealed class BusyProgressOverlay : IDisposable
{
    private readonly Form _owner;
    private readonly Form _shade;
    private readonly Form _dialog;
    private readonly Label _message;
    private readonly ProgressBar _progress;
    private bool _disposed;
    private bool _restoreOwnerToFront;
    private bool _showRequested;
    private string _lastMessage = "처리 중...";
    private bool _isDeterminate;
    private int _progressTotal = 1;
    private int _progressCompleted;

    public BusyProgressOverlay(Form owner)
    {
        _owner = owner;
        _shade = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.Black,
            Opacity = 0.38,
            ControlBox = false
        };
        _dialog = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedSingle,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.White,
            ClientSize = new Size(470, 145),
            ControlBox = false,
            MinimizeBox = false,
            MaximizeBox = false,
            Text = string.Empty
        };
        _message = new Label
        {
            Dock = DockStyle.Top,
            Height = 82,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("맑은 고딕", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(33, 46, 42),
            Padding = new Padding(16, 10, 16, 0)
        };
        _progress = new ProgressBar
        {
            Width = 390,
            Height = 24,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Location = new Point(39, 96)
        };
        var hint = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 20,
            Text = "조회가 끝날 때까지 잠시 기다려 주세요.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
            Font = new Font("맑은 고딕", 8F)
        };
        _dialog.Controls.Add(_progress);
        _dialog.Controls.Add(_message);
        _dialog.Controls.Add(hint);

        _shade.MouseDown += (_, _) => ActivateDialog();
        _owner.LocationChanged += OwnerBoundsChanged;
        _owner.SizeChanged += OwnerBoundsChanged;
        _owner.Activated += OwnerActivated;
    }

    public bool IsVisible => !_disposed && _dialog.Visible;

    public void Show(string message)
    {
        if (_disposed || !_owner.Visible) return;
        _showRequested = true;
        _lastMessage = message;
        if (!IsVisible)
        {
            _restoreOwnerToFront = Form.ActiveForm == _owner || _owner.ContainsFocus;
            if (!_restoreOwnerToFront) return;
        }
        UpdateMessage(message);
        if (_isDeterminate)
        {
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Minimum = 0;
            _progress.Maximum = Math.Max(1, _progressTotal);
            _progress.Value = Math.Clamp(_progressCompleted, 0, _progress.Maximum);
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 25;
        }
        UpdateBounds();

        if (!_shade.Visible) _shade.Show(_owner);
        if (!_dialog.Visible) _dialog.Show(_owner);
        _owner.Enabled = false;
        _shade.BringToFront();
        ActivateDialog();
    }

    public void Hide()
    {
        if (_disposed) return;
        _showRequested = false;
        _isDeterminate = false;
        _progressTotal = 1;
        _progressCompleted = 0;
        _progress.MarqueeAnimationSpeed = 0;
        if (!_owner.IsDisposed) _owner.Enabled = true;
        _dialog.Hide();
        _shade.Hide();
        if (_restoreOwnerToFront && !_owner.IsDisposed && _owner.Visible)
        {
            _owner.BringToFront();
            _owner.Activate();
        }
        _restoreOwnerToFront = false;
    }

    public void ConfigureProgress(int total)
    {
        if (_disposed) return;
        _isDeterminate = true;
        _progressTotal = Math.Max(1, total);
        _progressCompleted = 0;
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = 0;
    }

    public void ReportProgress(int completed, int total, string message)
    {
        if (_disposed) return;
        _isDeterminate = true;
        _progressTotal = Math.Max(1, total);
        _progressCompleted = Math.Clamp(completed, 0, _progressTotal);
        UpdateMessage(message);
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = Math.Clamp(completed, 0, _progress.Maximum);
    }

    public void UpdateMessage(string message)
    {
        if (_disposed) return;
        _lastMessage = message;
        _message.Text = message;
    }

    private void OwnerBoundsChanged(object? sender, EventArgs e)
    {
        if (IsVisible) UpdateBounds();
    }

    private void OwnerActivated(object? sender, EventArgs e)
    {
        if (_showRequested && !IsVisible) Show(_lastMessage);
    }

    private void UpdateBounds()
    {
        if (!_owner.IsHandleCreated) return;
        var clientBounds = _owner.RectangleToScreen(_owner.ClientRectangle);
        _shade.Bounds = clientBounds;
        _dialog.Location = new Point(
            clientBounds.Left + Math.Max(0, (clientBounds.Width - _dialog.Width) / 2),
            clientBounds.Top + Math.Max(0, (clientBounds.Height - _dialog.Height) / 2));
    }

    private void ActivateDialog()
    {
        if (!_dialog.Visible) return;
        _dialog.BringToFront();
        _dialog.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.LocationChanged -= OwnerBoundsChanged;
        _owner.SizeChanged -= OwnerBoundsChanged;
        _owner.Activated -= OwnerActivated;
        _dialog.Dispose();
        _shade.Dispose();
    }
}
