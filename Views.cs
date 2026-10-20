namespace AllinAIQuotaTray;

internal sealed class SummaryPanel : UserControl
{
    private readonly FlowLayoutPanel _stack;
    private readonly Font _titleFont = new(SystemFonts.MessageBoxFont!.FontFamily, 10, FontStyle.Bold);
    private readonly Font _amountFont = new(SystemFonts.MessageBoxFont!.FontFamily, 22, FontStyle.Bold);
    private readonly Font _percentFont = new(SystemFonts.MessageBoxFont!.FontFamily, 15, FontStyle.Bold);

    public SummaryPanel(SummaryContent content)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Menu;
        ForeColor = SystemColors.MenuText;
        TabStop = false;
        _stack = new()
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty,
            MinimumSize = new Size(360, 0), MaximumSize = new Size(360, 0)
        };
        Controls.Add(_stack);
        UpdateContent(content);
    }
    public void UpdateContent(SummaryContent content)
    {
        SuspendLayout();
        _stack.SuspendLayout();
        foreach (Control control in _stack.Controls.Cast<Control>().ToArray()) control.Dispose();
        _stack.Controls.Clear();
        AddText("AllinAI", _titleFont);
        if (content.Account.Length > 0) AddText(content.Account);
        AddText(content.Title, _titleFont);
        if (content.Banner is not null) AddText(content.Banner, null, SystemColors.HotTrack);
        AddText(content.Caption);
        var numbers = new TableLayoutPanel { AutoSize = true, Width = 328, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 2, 0, 6) };
        numbers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        numbers.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var amount = new Label
        {
            Text = content.Amount, AutoSize = true, Font = _amountFont, MaximumSize = new Size(content.Percentage is null ? 328 : 240, 0),
            ForeColor = content.IsWarning && !SystemInformation.HighContrast ? Color.FromArgb(155, 38, 30) : ForeColor,
            AccessibleName = content.Caption + " " + content.Amount, Margin = Padding.Empty
        };
        numbers.Controls.Add(amount, 0, 0);
        if (content.Percentage is not null)
            numbers.Controls.Add(new Label { Text = content.Percentage, Font = _percentFont, AutoSize = true,
                Margin = new Padding(4, 7, 0, 0), AccessibleName = "剩余 " + content.Percentage }, 1, 0);
        _stack.Controls.Add(numbers);
        if (content.Progress is { } progress) _stack.Controls.Add(new RemainingBar(progress) { Margin = new Padding(0, 0, 0, 8) });
        foreach (var line in content.Lines) AddText(line);
        AccessibleName = "账户概览";
        _stack.ResumeLayout(true);
        ResumeLayout(true);
    }
    private void AddText(string text, Font? font = null, Color? color = null)
    {
        if (text.Length == 0) return;
        _stack.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(328, 0),
            Font = font ?? Font, ForeColor = color ?? ForeColor, Margin = new Padding(0, 0, 0, 5), AccessibleName = text, UseMnemonic = false });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _titleFont.Dispose(); _amountFont.Dispose(); _percentFont.Dispose(); }
    }
}

internal sealed class RemainingBar : Control
{
    private readonly double _remaining;
    public RemainingBar(double remaining)
    {
        _remaining = Math.Clamp(remaining, 0, 1);
        Size = new Size(328, 6);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "剩余额度 " + Format.Percent(_remaining);
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var track = new SolidBrush(SystemColors.ControlDark);
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Highlight
            : _remaining <= 0.1 ? Color.FromArgb(155, 38, 30) : _remaining <= 0.3 ? Color.FromArgb(135, 85, 10) : SystemColors.Highlight);
        e.Graphics.FillRectangle(track, ClientRectangle);
        e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _remaining), Height);
    }
}

internal sealed class LoginForm : Form
{
    private readonly TextBox _email = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _error = new() { AutoSize = true, MaximumSize = new Size(350, 0), ForeColor = SystemColors.HotTrack };
    private readonly Button _submit = new() { Text = "登录", AutoSize = true };
    private readonly Func<string, string, CancellationToken, Task> _signIn;
    private CancellationTokenSource? _pending;

    public LoginForm(string email, Func<string, string, CancellationToken, Task> signIn)
    {
        _signIn = signIn;
        Text = "登录 AllinAI";
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(390, 290);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "邮箱", AutoSize = true });
        _email.Text = email;
        _email.AccessibleName = "AllinAI 邮箱";
        layout.Controls.Add(_email);
        layout.Controls.Add(new Label { Text = "密码", AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
        _password.AccessibleName = "AllinAI 密码";
        layout.Controls.Add(_password);
        layout.Controls.Add(new Label { Text = "登录验证成功后，密码保存在 Windows 凭据管理器。", AutoSize = true,
            MaximumSize = new Size(350, 0), Margin = new Padding(0, 12, 0, 4) });
        layout.Controls.Add(_error);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "取消", AutoSize = true };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(_submit);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _submit;
        CancelButton = cancel;
        _submit.Click += async (_, _) => await SubmitAsync();
        FormClosing += (_, _) => { _pending?.Cancel(); _password.Clear(); };
        Shown += (_, _) => { if (_email.Text.Length > 0) _password.Focus(); else _email.Focus(); };
    }
    private async Task SubmitAsync()
    {
        if (_pending is not null) return;
        if (string.IsNullOrWhiteSpace(_email.Text) || _password.Text.Length == 0)
        { _error.Text = "请输入邮箱与密码。"; return; }
        var pending = new CancellationTokenSource();
        _pending = pending;
        _submit.Enabled = _email.Enabled = _password.Enabled = false;
        _error.Text = "正在登录…";
        try
        {
            await _signIn(_email.Text.Trim(), _password.Text, pending.Token);
            if (!IsDisposed) { _password.Clear(); Close(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!IsDisposed) _error.Text = ClientError.From(error).Message; }
        finally
        {
            _pending = null;
            pending.Dispose();
            if (!IsDisposed) _submit.Enabled = _email.Enabled = _password.Enabled = true;
        }
    }
}
