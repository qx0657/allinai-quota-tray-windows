using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using Microsoft.Win32;

namespace AllinAIQuotaTray;

internal static class Program
{
#if !NATIVE_TESTING
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\AllinAIQuotaTray", out var firstInstance);
        if (!firstInstance) return;
        ApplicationConfiguration.Initialize();
        try { Application.Run(new TrayApplication()); }
        catch (Exception error)
        {
            MessageBox.Show(ClientError.From(error).Message, "AllinAI Quota 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
#endif
}

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly ContextMenuStrip _menu = new() { ShowImageMargin = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private readonly NotifyIcon _tray;
    private readonly AllinClient _client;
    private readonly ReleaseClient _releases;
    private readonly PreferenceStore _preferences;
    private readonly QuotaStore _store;
    private readonly ReminderManager _reminders;
    private readonly UpdateChecker _updates;
    private readonly string _directory;
    private readonly Queue<Notice> _notices = new();
    private DateTimeOffset _nextNoticeAt;
    private Icon? _icon;
    private LoginForm? _login;
    private SummaryPanel? _summary;
    private ToolStripMenuItem? _refreshItem;
    private bool _ticking;
    private bool _sleeping;
    private bool _exiting;
    private string? _localError;

    public TrayApplication()
    {
        _ = _dispatcher.Handle;
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllinAIQuotaTray");
        _preferences = new(_directory);
        _client = new(new LocalStorage(_directory, new WindowsCredentialStore()));
        _releases = new(AppInfo.Version);
        _store = new(_client, _preferences);
        _reminders = new(_preferences);
        _updates = new(_releases, _preferences, AppInfo.Version);
        _tray = new() { Text = "AllinAI Quota", ContextMenuStrip = _menu, Visible = true };
        _menu.Opening += (_, _) => RebuildMenu();
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // WinForms exposes the native tray menu on right-click only; retain its own dismissal/focus behavior on left-click.
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
            }
        };
        _store.Changed += StoreChanged;
        _updates.Changed += Render;
        NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        _timer.Tick += (_, _) => Run(TickAsync);
        Render();
        _timer.Start();
        Dispatch(() => Run(async () =>
        {
            await _store.StartAsync();
            await CheckAutomaticallyAsync();
        }));
    }

    private void Dispatch(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) when (_exiting || _dispatcher.IsDisposed) { }
    }
    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_exiting) ShowError(ClientError.From(error).Message); }
    }
    private void StoreChanged()
    {
        Render();
        try
        {
            foreach (var notice in _reminders.Evaluate(_store, DateTimeOffset.UtcNow)) _notices.Enqueue(notice);
            ShowNextNotice();
        }
        catch (Exception error) { _localError = ClientError.From(error).Message; }
    }
    private async Task TickAsync()
    {
        if (_ticking || _sleeping || _exiting) return;
        _ticking = true;
        try
        {
            if (_store.NextRefreshAt <= DateTimeOffset.UtcNow) await _store.RefreshAsync();
            await CheckAutomaticallyAsync();
            ShowNextNotice();
            if (_menu.Visible) Render();
        }
        finally { _ticking = false; }
    }
    private async Task CheckAutomaticallyAsync()
    {
        try { await _updates.CheckAutomaticallyIfNeededAsync(); }
        catch (Exception error) { _localError = ClientError.From(error).Message; Render(); }
    }
    private void ShowNextNotice()
    {
        if (!_preferences.Value.RemindersEnabled) { _notices.Clear(); return; }
        if (_notices.Count == 0 || DateTimeOffset.UtcNow < _nextNoticeAt || _sleeping) return;
        var notice = _notices.Dequeue();
        try { _tray.ShowBalloonTip(10000, notice.Title, notice.Body, ToolTipIcon.Warning); }
        catch { _reminders.Retry(notice); }
        _nextNoticeAt = DateTimeOffset.UtcNow.AddSeconds(30);
    }
    private void NetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Dispatch(() => Run(_store.RecoverConnectionAsync));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatch(() =>
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _sleeping = true;
            _login?.Close();
            _store.Suspend();
            _updates.Stop();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _sleeping = false;
            Run(async () => { await _store.ResumeAsync(); await CheckAutomaticallyAsync(); });
        }
    });

    private SummaryContent Content()
    {
        var content = SummaryContent.Make(_store, DateTimeOffset.UtcNow);
        if (_localError is not null) content.Lines.Add(_localError);
        return content;
    }
    private void Render()
    {
        if (_exiting) return;
        var content = Content();
        var icon = ProgressIcon.Make(content);
        _tray.Icon = icon;
        _icon?.Dispose();
        _icon = icon;
        var updateText = _updates.AvailableRelease is { } available ? $" · 有新版本 {available.TagName}" : "";
        var stale = _store.Snapshot.SubscriptionError is not null || _store.Snapshot.ProfileError is not null || _store.Error is not null;
        var tooltip = (stale ? "数据待更新 · " : "") + $"{Format.Shorten(content.Title, 18)} · {content.Amount} {content.Percentage}";
        if (_store.Snapshot.Profile?.ValidBalance is { } balance && _store.Status.Mode == ConsumptionMode.Subscription)
            tooltip += $" · 余额 ${Format.Money(balance)}";
        _tray.Text = Format.Shorten(tooltip + updateText, 63);
        if (_menu.Visible)
        {
            _summary?.UpdateContent(content);
            if (_refreshItem is not null) _refreshItem.Enabled = _store.CanRefresh;
        }
    }

    private void RebuildMenu()
    {
        _summary = null;
        _refreshItem = null;
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _menu.Items.Clear();
        _summary = new(Content());
        _menu.Items.Add(new ToolStripControlHost(_summary) { AutoSize = true, Margin = Padding.Empty, Padding = Padding.Empty });
        _menu.Items.Add(new ToolStripSeparator());
        if (_store.IsConfigured)
        {
            _refreshItem = Item("立即刷新", _store.RefreshAsync, Keys.Control | Keys.R);
            _refreshItem.Enabled = _store.CanRefresh;
            _menu.Items.Add(_refreshItem);
            if (_store.NeedsLogin) _menu.Items.Add(Item("重新登录…", ConfigureAccount));
        }
        else _menu.Items.Add(Item("登录…", ConfigureAccount));
        _menu.Items.Add(Item("打开订阅页", () => OpenUrl(AppDefaults.SubscriptionPage), Keys.Control | Keys.O));
        AddSubscriptionDetails();
        _menu.Items.Add(new ToolStripSeparator());
        var intervals = new ToolStripMenuItem($"刷新间隔 · {_store.Config.RefreshIntervalSeconds / 60} 分钟");
        foreach (var seconds in AppDefaults.Intervals)
        {
            var item = Item($"{seconds / 60} 分钟", () => _store.SetInterval(seconds));
            item.Checked = _store.Config.RefreshIntervalSeconds == seconds;
            intervals.DropDownItems.Add(item);
        }
        _menu.Items.Add(intervals);
        var startup = Item("开机自启动", () => StartupManager.SetEnabled(!StartupManager.IsEnabled()));
        try { startup.Checked = StartupManager.IsEnabled(); }
        catch { startup.Enabled = false; }
        _menu.Items.Add(startup);
        var tools = new ToolStripMenuItem("账号与工具");
        if (_store.IsConfigured)
        {
            tools.DropDownItems.Add(Item("重新登录…", ConfigureAccount));
            tools.DropDownItems.Add(Item("退出登录…", Logout));
            tools.DropDownItems.Add(new ToolStripSeparator());
        }
        tools.DropDownItems.Add(ReminderMenu());
        tools.DropDownItems.Add(Item("打开配置文件夹", () =>
        {
            Directory.CreateDirectory(_directory);
            Process.Start(new ProcessStartInfo(_directory) { UseShellExecute = true });
        }));
        _menu.Items.Add(tools);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"版本：v{AppInfo.Version}") { Enabled = false });
        _menu.Items.Add(AboutMenu());
        _menu.Items.Add(Item("退出", ExitThread));
    }

    private void AddSubscriptionDetails()
    {
        if (_store.Snapshot.Subscriptions is not { Count: > 0 } subscriptions) return;
        var details = new ToolStripMenuItem("订阅详情");
        foreach (var sub in subscriptions)
        {
            var entry = new ToolStripMenuItem($"{Format.Shorten(sub.Group?.Name ?? "订阅", 50)} · {Format.Status(sub.Status)}");
            var content = new SummaryContent
            {
                Title = sub.Group?.Name ?? "订阅",
                Caption = $"{sub.Group?.Platform ?? "平台未提供"} · {Format.Status(sub.Status)}",
                Amount = "订阅详情",
                Lines = QuotaPolicy.Metrics(sub).Select(m => $"{m.Label}：已用 ${Format.Money(m.Used)} / ${Format.Money(m.Limit)}").ToList()
            };
            if (content.Lines.Count == 0) content.Lines.Add("额度信息暂不可确认");
            if (Format.Date(sub.StartsAt) is { } starts) content.Lines.Add($"开始：{Format.Timestamp(starts, true)}");
            if (Format.Date(sub.ExpiresAt) is { } expires) content.Lines.Add($"到期：{Format.Timestamp(expires, true)}");
            if (!string.IsNullOrWhiteSpace(sub.Group?.Description)) content.Lines.Add(Format.Shorten(sub.Group.Description, 160));
            entry.DropDownItems.Add(new ToolStripControlHost(new SummaryPanel(content)) { AutoSize = true });
            if (QuotaPolicy.IsActive(sub, DateTimeOffset.UtcNow) && sub.Id is { } id)
            {
                var select = Item("在托盘显示此订阅", () => _store.SelectSubscription(id));
                select.Checked = _store.Status.Subscription?.Id == id;
                entry.DropDownItems.Add(select);
            }
            if (sub.Group?.Description is { Length: > 160 } description)
                entry.DropDownItems.Add(Item("完整说明…", () => ShowDescription(description)));
            details.DropDownItems.Add(entry);
        }
        _menu.Items.Add(details);
    }
    private ToolStripMenuItem ReminderMenu()
    {
        var menu = new ToolStripMenuItem("提醒设置");
        var enabled = Item("开启额度与到期提醒", () => SavePreferences(() =>
        {
            _preferences.Value.RemindersEnabled = !_preferences.Value.RemindersEnabled;
            _notices.Clear();
        }));
        enabled.Checked = _preferences.Value.RemindersEnabled;
        menu.DropDownItems.Add(enabled);
        menu.DropDownItems.Add(new ToolStripMenuItem("到期前 3 天／1 天、剩余额度 10% 时提醒") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var threshold in new[] { 0, 1, 5, 10 })
        {
            var item = Item(threshold == 0 ? "关闭低余额提醒" : $"余额不高于 ${threshold} 时提醒",
                () => SavePreferences(() => _preferences.Value.LowBalanceThreshold = threshold));
            item.Checked = _preferences.Value.LowBalanceThreshold == threshold;
            menu.DropDownItems.Add(item);
        }
        return menu;
    }
    private ToolStripMenuItem AboutMenu()
    {
        var menu = new ToolStripMenuItem(_updates.AvailableRelease is null ? "关于 AllinAI Quota" : "关于 AllinAI Quota · 有新版本");
        var check = Item(_updates.IsChecking ? "正在检查更新…" : "检查更新…", CheckUpdatesAsync);
        check.Enabled = !_updates.IsChecking;
        menu.DropDownItems.Add(check);
        if (_updates.AvailableRelease is { } release)
            menu.DropDownItems.Add(Item($"下载 {release.TagName}…", () => OpenUrl(release.PageUrl)));
        var automatic = Item("自动检查更新（每天）", async () =>
        {
            _updates.SetAutomaticChecks(!_preferences.Value.AutomaticUpdateChecks);
            await CheckAutomaticallyAsync();
        });
        automatic.Checked = _preferences.Value.AutomaticUpdateChecks;
        menu.DropDownItems.Add(automatic);
        if (_preferences.Value.LastUpdateCheck is { } last)
            menu.DropDownItems.Add(new ToolStripMenuItem($"上次检查：{Format.Timestamp(last, true)}") { Enabled = false });
        if (_updates.State.Kind == UpdateStateKind.Failed)
            menu.DropDownItems.Add(new ToolStripMenuItem("上次检查失败") { Enabled = false, ToolTipText = _updates.State.Message });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(Item("更新记录…", () => OpenUrl(AppDefaults.Repository + "/releases")));
        menu.DropDownItems.Add(Item("开源地址（GitHub）…", () => OpenUrl(AppDefaults.Repository)));
        return menu;
    }
    private void SavePreferences(Action update) { update(); _preferences.Save(); _localError = null; Render(); }
    private void ConfigureAccount()
    {
        if (_login is { IsDisposed: false }) { _login.Activate(); return; }
        _login = new LoginForm(_store.Config.Email, async (email, password, cancellation) =>
        {
            _notices.Clear();
            await _store.SignInAsync(email, password, cancellation);
        });
        _login.FormClosed += (_, _) => _login = null;
        _login.Show();
        _login.Activate();
    }
    private void Logout()
    {
        if (MessageBox.Show("这会清除本地令牌和 Windows 凭据管理器中的密码。邮箱、刷新间隔与偏好会保留。", "退出登录？",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _login?.Close();
        _notices.Clear();
        _store.Logout();
    }
    private async Task CheckUpdatesAsync()
    {
        var result = await _updates.CheckAsync();
        if (_exiting || result.Kind == UpdateStateKind.Idle) return;
        switch (result.Kind)
        {
            case UpdateStateKind.Available:
                if (MessageBox.Show($"当前版本：v{AppInfo.Version}\n新版本：{result.Release!.TagName}\n打开 GitHub Release 下载 Windows 版本，退出旧版后替换。",
                    "发现新版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK) OpenUrl(result.Release.PageUrl);
                break;
            case UpdateStateKind.Current:
                MessageBox.Show($"当前版本：v{AppInfo.Version}\n最新正式版：{result.Release!.TagName}", "当前无需更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case UpdateStateKind.NoRelease:
            case UpdateStateKind.Failed:
                if (MessageBox.Show((result.Message ?? "GitHub 上尚未找到正式 Release。") + "\n打开更新记录查看？",
                    result.Kind == UpdateStateKind.NoRelease ? "暂无正式发布版本" : "检查更新失败", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                    OpenUrl(AppDefaults.Repository + "/releases");
                break;
        }
    }
    private ToolStripMenuItem Item(string title, Action action, Keys shortcut = Keys.None) => Item(title, () => { action(); return Task.CompletedTask; }, shortcut);
    private ToolStripMenuItem Item(string title, Func<Task> action, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(title) { ShortcutKeys = shortcut };
        item.Click += (_, _) => Run(action);
        return item;
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private static void ShowError(string message) => MessageBox.Show(message, "AllinAI Quota", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private static void ShowDescription(string description)
    {
        var form = new Form { Text = "订阅完整说明", ClientSize = new Size(520, 360), StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false };
        form.Controls.Add(new TextBox { Text = description, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true });
        form.Show();
    }
    protected override void ExitThreadCore()
    {
        _exiting = true;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        _timer.Stop();
        _store.Suspend();
        _updates.Stop();
        _login?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        _menu.Dispose();
        _timer.Dispose();
        _client.Dispose();
        _releases.Dispose();
        _dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
