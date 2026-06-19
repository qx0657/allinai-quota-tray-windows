using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace AllinAIQuotaTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication());
    }
}

internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly AllinClient _client;
    private readonly string _appDataDir;

    private Config _config = new();
    private FetchState _state = FetchState.Idle();
    private bool _isFetching;
    private Icon? _currentIcon;

    public TrayApplication()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AllinAIQuotaTray");
        _client = new AllinClient(_appDataDir);

        try
        {
            _client.EnsureFiles();
            _config = _client.ReadConfig();
        }
        catch (Exception ex)
        {
            _state = FetchState.Failed($"配置初始化失败：{ex.Message}", null);
        }

        _menu.Opening += (_, _) => RebuildMenu();
        _refreshTimer.Tick += async (_, _) => await RefreshNowAsync();

        _trayIcon = new NotifyIcon
        {
            Text = "AllinAI Quota",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _trayIcon.MouseUp += TrayIconOnMouseUp;

        SetIcon(null, Color.Gray);
        RebuildMenu();
        ScheduleTimer();

        if (_client.HasConfiguredCredentials())
        {
            _ = RefreshNowAsync();
        }
    }

    private void TrayIconOnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            RebuildMenu();
            typeof(NotifyIcon)
                .GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(_trayIcon, null);
        }
    }

    private async Task RefreshNowAsync(bool forceLogin = false)
    {
        if (_isFetching)
        {
            return;
        }

        _isFetching = true;
        _state = FetchState.Loading();
        SetIcon(null, Color.RoyalBlue);
        RebuildMenu();

        try
        {
            var (subscriptions, profile) = await _client.FetchAccountStatusAsync(forceLogin);
            var now = DateTimeOffset.Now;
            var active = subscriptions.FirstOrDefault(item => item.Status == "active") ?? subscriptions.FirstOrDefault();

            if (active is null)
            {
                _state = FetchState.Empty(now, profile?.Balance, profile?.DisplayName);
                SetIcon(0, Color.Gray);
            }
            else
            {
                var summary = new SubscriptionSummary(
                    active,
                    BestMetric(active),
                    profile?.DisplayName,
                    profile?.Balance,
                    now);
                _state = FetchState.Loaded(summary);
                ApplyIcon(summary);
            }
        }
        catch (Exception ex)
        {
            _state = FetchState.Failed(ex.Message, DateTimeOffset.Now);
            SetIcon(null, Color.Firebrick);
        }
        finally
        {
            _isFetching = false;
            RebuildMenu();
        }
    }

    private void RebuildMenu()
    {
        UpdateTooltip();
        _menu.Items.Clear();
        var hasCredentials = _client.HasConfiguredCredentials();

        if (hasCredentials)
        {
            switch (_state.Kind)
            {
                case FetchStateKind.Idle:
                    AddDisabled("未加载");
                    break;
                case FetchStateKind.Loading:
                    AddDisabled("正在刷新...");
                    break;
                case FetchStateKind.Empty:
                    AddBalance(_state.Balance);
                    AddDisabled("没有有效订阅");
                    AddDisabled($"更新于 {FormatTime(_state.FetchedAt ?? DateTimeOffset.Now)}");
                    break;
                case FetchStateKind.Failed:
                    AddDisabled("获取失败");
                    AddDisabled(_state.Message ?? "未知错误");
                    if (_state.FetchedAt is { } failedAt)
                    {
                        AddDisabled($"更新于 {FormatTime(failedAt)}");
                    }
                    break;
                case FetchStateKind.Loaded:
                    if (_state.Summary is { } summary)
                    {
                        AddDetails(summary);
                    }
                    break;
            }
        }
        else
        {
            AddDisabled("请登录");
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (hasCredentials)
        {
            _menu.Items.Add(Item(LogoutTitle(), async () => await LogoutAsync()));
            _menu.Items.Add(Item("立即刷新", async () => await RefreshNowAsync()));
        }
        else
        {
            _menu.Items.Add(Item("登录...", async () => await ConfigureAccountAsync()));
        }

        _menu.Items.Add(Item("打开订阅页", OpenSubscriptions));
        _menu.Items.Add(RefreshIntervalMenu());
        _menu.Items.Add(StartupMenuItem());
        _menu.Items.Add(Item("打开配置文件夹", OpenConfigFolder));
        _menu.Items.Add(new ToolStripSeparator());
        AddDisabled($"版本：v{AppInfo.Version}");
        _menu.Items.Add(Item("退出", ExitThread));
    }

    private void AddDetails(SubscriptionSummary summary)
    {
        var sub = summary.Subscription;
        var group = sub.Group;

        AddBalance(summary.Balance);
        AddDisabled(group?.Name ?? "订阅");

        if (!string.IsNullOrWhiteSpace(group?.Platform))
        {
            AddDisabled($"平台：{group.Platform}");
        }

        if (!string.IsNullOrWhiteSpace(group?.Description))
        {
            AddDisabled(group.Description);
        }

        AddDisabled($"状态：{StatusText(sub.Status)}");

        if (ParseDate(sub.ExpiresAt) is { } expiresAt)
        {
            AddDisabled($"到期时间：{FormatDate(expiresAt)}");
            AddDisabled($"剩余时间：{RemainingText(expiresAt)}");
        }

        if (summary.Metric is { } metric)
        {
            AddDisabled($"{metric.Label}：${Money(metric.Used)} / ${Money(metric.Limit)}");
            AddDisabled($"已用：{Math.Round(metric.Ratio * 100):0}%");

            if (ResetDate(metric.WindowStart, metric.Hours) is { } resetAt)
            {
                AddDisabled($"重置倒计时：{RemainingText(resetAt)}");
            }
        }
        else
        {
            AddDisabled("额度：无限或不可用");
        }

        AddDisabled($"更新于：{FormatTime(summary.FetchedAt)}");
    }

    private void AddBalance(double? balance)
    {
        AddDisabled(balance.HasValue ? $"当前余额：${Money(balance.Value)}" : "当前余额：不可用");
        _menu.Items.Add(new ToolStripSeparator());
    }

    private ToolStripMenuItem RefreshIntervalMenu()
    {
        var root = new ToolStripMenuItem("刷新间隔");
        foreach (var seconds in new[] { 60, 300, 600, 1800 })
        {
            var title = seconds < 60 ? $"{seconds} 秒" : $"{seconds / 60} 分钟";
            var item = new ToolStripMenuItem(title)
            {
                Checked = _config.RefreshIntervalSeconds == seconds,
                Tag = seconds
            };
            item.Click += (_, _) =>
            {
                _config.RefreshIntervalSeconds = seconds;
                _client.WriteConfig(_config);
                ScheduleTimer();
                RebuildMenu();
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    private ToolStripMenuItem StartupMenuItem()
    {
        var item = new ToolStripMenuItem("开机自启动")
        {
            Checked = StartupManager.IsEnabled()
        };
        item.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(!StartupManager.IsEnabled());
                RebuildMenu();
            }
            catch (Exception ex)
            {
                ShowError("设置开机自启动失败", ex.Message);
            }
        };
        return item;
    }

    private async Task ConfigureAccountAsync()
    {
        try
        {
            _config = _client.ReadConfig();
        }
        catch (Exception ex)
        {
            ShowError("配置错误", ex.Message);
            return;
        }

        using var form = new LoginForm(_config.Email);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            _client.SaveCredentials(form.Email, form.Password);
            _config = _client.ReadConfig();
            _state = FetchState.Idle();
            ScheduleTimer();
            RebuildMenu();
            await RefreshNowAsync(forceLogin: true);
        }
        catch (Exception ex)
        {
            ShowError("保存失败", ex.Message);
        }
    }

    private async Task LogoutAsync()
    {
        var result = MessageBox.Show(
            "这会从 Windows 凭据管理器移除保存的密码，并清除本地 token 缓存。邮箱和刷新间隔会保留在本地配置中。",
            "退出登录？",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.OK)
        {
            return;
        }

        try
        {
            _client.Logout();
            _refreshTimer.Stop();
            _state = FetchState.Failed("请登录", null);
            SetIcon(null, Color.Gray);
            RebuildMenu();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ShowError("退出登录失败", ex.Message);
        }
    }

    private void OpenSubscriptions()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://allincode.top/subscriptions",
            UseShellExecute = true
        });
    }

    private void OpenConfigFolder()
    {
        Directory.CreateDirectory(_appDataDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = _appDataDir,
            UseShellExecute = true
        });
    }

    private void ScheduleTimer()
    {
        _refreshTimer.Stop();
        if (string.IsNullOrWhiteSpace(_config.Email))
        {
            return;
        }

        _refreshTimer.Interval = Math.Max(1000, _config.RefreshIntervalSeconds * 1000);
        _refreshTimer.Start();
    }

    private static QuotaMetric? BestMetric(Subscription sub)
    {
        var metrics = new[]
        {
            new QuotaMetric("每日", sub.DailyUsageUsd ?? 0, sub.Group?.DailyLimitUsd ?? 0, sub.DailyWindowStart, 24),
            new QuotaMetric("每周", sub.WeeklyUsageUsd ?? 0, sub.Group?.WeeklyLimitUsd ?? 0, sub.WeeklyWindowStart, 168),
            new QuotaMetric("每月", sub.MonthlyUsageUsd ?? 0, sub.Group?.MonthlyLimitUsd ?? 0, sub.MonthlyWindowStart, 720)
        };
        return metrics.FirstOrDefault(metric => metric.Limit > 0);
    }

    private void ApplyIcon(SubscriptionSummary summary)
    {
        if (summary.Subscription.Status != "active")
        {
            SetIcon(null, Color.Firebrick);
            return;
        }

        if (summary.Metric is not { } metric)
        {
            SetIcon(1, Color.ForestGreen);
            return;
        }

        var color = metric.Ratio switch
        {
            >= 0.9 => Color.Firebrick,
            >= 0.7 => Color.DarkOrange,
            _ => Color.ForestGreen
        };
        SetIcon(metric.Ratio, color);
    }

    private void SetIcon(double? progress, Color color)
    {
        var icon = ProgressIcon.Make(progress, color);
        _trayIcon.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private string LogoutTitle()
    {
        var displayName = _state.Kind switch
        {
            FetchStateKind.Loaded => _state.Summary?.UserDisplayName,
            FetchStateKind.Empty => _state.UserDisplayName,
            _ => null
        };
        displayName = string.IsNullOrWhiteSpace(displayName) ? _config.Email : displayName;
        return string.IsNullOrWhiteSpace(displayName) ? "退出登录" : $"退出登录（{displayName}）";
    }

    private static ToolStripMenuItem Item(string title, Action action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripMenuItem Item(string title, Func<Task> action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += async (_, _) => await action();
        return item;
    }

    private void AddDisabled(string title)
    {
        _menu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date.ToLocalTime()
            : null;
    }

    private static DateTimeOffset? ResetDate(string? start, int hours)
    {
        return ParseDate(start)?.AddHours(hours);
    }

    private static string RemainingText(DateTimeOffset until)
    {
        var remaining = until - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "已过期";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟";
        }

        return $"{Math.Max(1, remaining.Minutes)} 分钟";
    }

    private static string StatusText(string? status)
    {
        return status switch
        {
            "active" => "有效",
            "expired" => "已过期",
            { Length: > 0 } => status,
            _ => "-"
        };
    }

    private static string FormatDate(DateTimeOffset date)
    {
        return date.ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatTime(DateTimeOffset date)
    {
        return date.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private static string Money(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void UpdateTooltip()
    {
        var text = _state.Kind switch
        {
            FetchStateKind.Loaded when _state.Summary is { } summary => LoadedTooltip(summary),
            FetchStateKind.Empty => $"AllinAI 无有效订阅 | {BalanceTooltip(_state.Balance)}",
            FetchStateKind.Failed => $"AllinAI 获取失败 | {_state.Message ?? "未知错误"}",
            FetchStateKind.Loading => "AllinAI 正在刷新...",
            _ => string.IsNullOrWhiteSpace(_config.Email) ? "AllinAI 请登录" : "AllinAI 未加载"
        };
        _trayIcon.Text = TrimTooltip(text);
    }

    private static string LoadedTooltip(SubscriptionSummary summary)
    {
        var quota = summary.Metric is { } metric
            ? $"剩余额度 {Math.Round((1 - metric.Ratio) * 100):0}%"
            : "额度不限";
        return $"AllinAI {quota} | {BalanceTooltip(summary.Balance)}";
    }

    private static string BalanceTooltip(double? balance)
    {
        return balance.HasValue ? $"余额 ${Money(balance.Value)}" : "余额不可用";
    }

    private static string TrimTooltip(string text)
    {
        const int maxLength = 63;
        return text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 3), "...");
    }

    private static void ShowError(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void ExitThreadCore()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class AllinClient
{
    private static readonly Uri BaseUri = new("https://allincode.top/api/v1/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _configPath;
    private readonly string _tokenPath;

    public AllinClient(string appDataDir)
    {
        AppDataDir = appDataDir;
        _configPath = Path.Combine(appDataDir, "config.json");
        _tokenPath = Path.Combine(appDataDir, "tokens.json");
    }

    private string AppDataDir { get; }

    public void EnsureFiles()
    {
        Directory.CreateDirectory(AppDataDir);
        if (!File.Exists(_configPath))
        {
            WriteConfig(new Config());
            return;
        }

        MigrateLegacyConfigIfNeeded();
    }

    public Config ReadConfig()
    {
        EnsureFiles();
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath), JsonOptions) ?? new Config();
        config.RefreshIntervalSeconds = ClampRefreshInterval(config.RefreshIntervalSeconds);
        config.Email = config.Email.Trim();
        return config;
    }

    public void WriteConfig(Config config)
    {
        Directory.CreateDirectory(AppDataDir);
        config.RefreshIntervalSeconds = ClampRefreshInterval(config.RefreshIntervalSeconds);
        config.Email = config.Email.Trim();
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, JsonOptions), Encoding.UTF8);
    }

    public void SaveCredentials(string email, string password)
    {
        var config = ReadConfig();
        config.Email = email.Trim();
        WriteConfig(config);
        CredentialVault.Set(config.Email, password);
        ClearTokenCache();
    }

    public bool HasConfiguredCredentials()
    {
        try
        {
            var config = ReadConfig();
            return !string.IsNullOrWhiteSpace(config.Email) &&
                   !string.IsNullOrEmpty(CredentialVault.Get(config.Email));
        }
        catch
        {
            return false;
        }
    }

    public void Logout()
    {
        var config = ReadConfig();
        if (!string.IsNullOrWhiteSpace(config.Email))
        {
            CredentialVault.Delete(config.Email);
        }

        ClearTokenCache();
    }

    public async Task<(List<Subscription> Subscriptions, UserProfile? Profile)> FetchAccountStatusAsync(bool forceLogin)
    {
        var token = await ValidAccessTokenAsync(forceLogin);
        try
        {
            var subscriptions = await RequestSubscriptionsAsync(token);
            var profile = await TryRequestUserProfileAsync(token);
            return (subscriptions, profile);
        }
        catch
        {
            var freshToken = await ValidAccessTokenAsync(forceLogin: true);
            var subscriptions = await RequestSubscriptionsAsync(freshToken);
            var profile = await TryRequestUserProfileAsync(freshToken);
            return (subscriptions, profile);
        }
    }

    private async Task<string> ValidAccessTokenAsync(bool forceLogin)
    {
        var cache = ReadTokenCache();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!forceLogin && cache is not null && cache.ExpiresAt > now + 60)
        {
            return cache.AccessToken;
        }

        if (!forceLogin && cache is not null && !string.IsNullOrWhiteSpace(cache.RefreshToken))
        {
            try
            {
                return await RefreshTokenAsync(cache.RefreshToken);
            }
            catch
            {
                return await LoginAsync();
            }
        }

        return await LoginAsync();
    }

    private async Task<string> LoginAsync()
    {
        var config = ReadConfig();
        if (string.IsNullOrWhiteSpace(config.Email))
        {
            throw new InvalidOperationException("请先登录");
        }

        var password = CredentialVault.Get(config.Email);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("请先输入密码");
        }

        var response = await PostAsync<LoginData>("auth/login", new { email = config.Email, password });
        SaveToken(response);
        return response.AccessToken;
    }

    private async Task<string> RefreshTokenAsync(string refreshToken)
    {
        var response = await PostAsync<LoginData>("auth/refresh", new { refresh_token = refreshToken });
        SaveToken(response);
        return response.AccessToken;
    }

    private async Task<List<Subscription>> RequestSubscriptionsAsync(string accessToken)
    {
        var timezone = Uri.EscapeDataString(TimeZoneNames.LocalIanaName());
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"subscriptions?timezone={timezone}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var envelope = await SendAsync<List<Subscription>>(request, "请求失败");
        return envelope ?? [];
    }

    private async Task<UserProfile?> TryRequestUserProfileAsync(string accessToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "user/profile"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await SendAsync<UserProfile>(request, "余额请求失败");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T> PostAsync<T>(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        var payload = await SendAsync<T>(request, "登录请求失败");
        return payload ?? throw new InvalidOperationException("响应为空");
    }

    private static async Task<T?> SendAsync<T>(HttpRequestMessage request, string httpError)
    {
        using var response = await Http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{httpError}（HTTP {(int)response.StatusCode}）");
        }

        var envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(content, JsonOptions)
                       ?? throw new InvalidOperationException("响应为空");
        if (envelope.Code.HasValue && envelope.Code.Value != 0)
        {
            throw new InvalidOperationException(envelope.Message ?? "API error");
        }

        return envelope.Data;
    }

    private void SaveToken(LoginData loginData)
    {
        var cache = new TokenCache
        {
            AccessToken = loginData.AccessToken,
            RefreshToken = loginData.RefreshToken ?? string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (loginData.ExpiresIn ?? 3600)
        };
        File.WriteAllText(_tokenPath, JsonSerializer.Serialize(cache, JsonOptions), Encoding.UTF8);
    }

    private TokenCache? ReadTokenCache()
    {
        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TokenCache>(File.ReadAllText(_tokenPath), JsonOptions);
    }

    private void ClearTokenCache()
    {
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
    }

    private void MigrateLegacyConfigIfNeeded()
    {
        var legacy = JsonSerializer.Deserialize<LegacyConfig>(File.ReadAllText(_configPath), JsonOptions);
        if (legacy is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(legacy.Email) && !string.IsNullOrWhiteSpace(legacy.Password))
        {
            CredentialVault.Set(legacy.Email.Trim(), legacy.Password);
            WriteConfig(new Config
            {
                Email = legacy.Email.Trim(),
                RefreshIntervalSeconds = ClampRefreshInterval(legacy.RefreshIntervalSeconds ?? 300)
            });
        }
    }

    private static int ClampRefreshInterval(int seconds)
    {
        return seconds is 60 or 300 or 600 or 1800 ? seconds : 300;
    }
}

internal sealed class LoginForm : Form
{
    private readonly TextBox _email = new();
    private readonly TextBox _password = new();

    public LoginForm(string currentEmail)
    {
        Text = "登录 AllinAI";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 170);

        var info = new Label
        {
            Text = "账号信息仅保存在本机，密码会写入 Windows 凭据管理器。",
            AutoSize = false,
            Location = new Point(18, 16),
            Size = new Size(384, 24)
        };

        var emailLabel = new Label { Text = "邮箱", Location = new Point(34, 55), AutoSize = true };
        _email.Location = new Point(92, 51);
        _email.Size = new Size(292, 27);
        _email.Text = currentEmail;

        var passwordLabel = new Label { Text = "密码", Location = new Point(34, 92), AutoSize = true };
        _password.Location = new Point(92, 88);
        _password.Size = new Size(292, 27);
        _password.UseSystemPasswordChar = true;

        var save = new Button { Text = "保存", Location = new Point(228, 128), Size = new Size(75, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(309, 128), Size = new Size(75, 28) };

        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        Controls.AddRange([info, emailLabel, _email, passwordLabel, _password, save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public string Email => _email.Text.Trim();

    public string Password => _password.Text;

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        {
            MessageBox.Show("请输入邮箱和密码。", "信息不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
    }
}

internal static class ProgressIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Make(double? progress, Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var rect = new RectangleF(3.5f, 3.5f, 25, 25);
        using var darkOutline = new Pen(Color.FromArgb(220, 0, 0, 0), 5.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var lightTrack = new Pen(Color.FromArgb(245, 255, 255, 255), 4.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        graphics.DrawEllipse(darkOutline, rect);
        graphics.DrawEllipse(lightTrack, rect);

        if (progress.HasValue)
        {
            using var pen = new Pen(color, 5.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(pen, rect, -90, (float)(Math.Clamp(progress.Value, 0, 1) * 360));
        }
        else
        {
            using var outerBrush = new SolidBrush(Color.FromArgb(235, 0, 0, 0));
            using var innerBrush = new SolidBrush(color);
            using var innerBorder = new Pen(Color.FromArgb(250, 255, 255, 255), 2.4f);
            graphics.FillEllipse(outerBrush, 5.5f, 5.5f, 21, 21);
            graphics.FillEllipse(innerBrush, 8.0f, 8.0f, 16, 16);
            graphics.DrawEllipse(innerBorder, 8.0f, 8.0f, 16, 16);
        }

        if (progress.HasValue)
        {
            using var brush = new SolidBrush(color);
            using var centerBorder = new Pen(Color.FromArgb(240, 0, 0, 0), 1.5f);
            graphics.FillEllipse(brush, 12.2f, 12.2f, 7.6f, 7.6f);
            graphics.DrawEllipse(centerBorder, 12.2f, 12.2f, 7.6f, 7.6f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

internal static class StartupManager
{
    private const string AppName = "AllinAIQuotaTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(AppName) as string;
        return string.Equals(Unquote(value), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                       ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。");
        if (enabled)
        {
            key.SetValue(AppName, Quote(Application.ExecutablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }

    private static string? Unquote(string? value)
    {
        return value?.Trim().Trim('"');
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Application.ProductVersion;
}

internal static class CredentialVault
{
    private const string Service = "local.allinai.quota.tray.windows";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref NativeCredential credential, [In] uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    public static void Set(string account, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredPersistLocalMachine,
                UserName = account
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"保存密码到 Windows 凭据管理器失败：{Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? Get(string account)
    {
        if (!CredRead(Target(account), CredTypeGeneric, 0, out var credentialPtr))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public static void Delete(string account)
    {
        if (CredDelete(Target(account), CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new InvalidOperationException($"从 Windows 凭据管理器移除密码失败：{error}");
        }
    }

    private static string Target(string account)
    {
        return $"{Service}:{account}";
    }
}

internal static class TimeZoneNames
{
    private static readonly Dictionary<string, string> WindowsToIana = new(StringComparer.OrdinalIgnoreCase)
    {
        ["China Standard Time"] = "Asia/Shanghai",
        ["UTC"] = "UTC",
        ["Pacific Standard Time"] = "America/Los_Angeles",
        ["Mountain Standard Time"] = "America/Denver",
        ["Central Standard Time"] = "America/Chicago",
        ["Eastern Standard Time"] = "America/New_York",
        ["GMT Standard Time"] = "Europe/London",
        ["W. Europe Standard Time"] = "Europe/Berlin",
        ["Tokyo Standard Time"] = "Asia/Tokyo",
        ["Korea Standard Time"] = "Asia/Seoul",
        ["Singapore Standard Time"] = "Asia/Singapore"
    };

    public static string LocalIanaName()
    {
        var id = TimeZoneInfo.Local.Id;
        return WindowsToIana.TryGetValue(id, out var iana) ? iana : id;
    }
}

internal sealed class Config
{
    public string Email { get; set; } = string.Empty;
    public int RefreshIntervalSeconds { get; set; } = 300;
}

internal sealed class LegacyConfig
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public int? RefreshIntervalSeconds { get; set; }
}

internal sealed class TokenCache
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
}

internal sealed class ApiEnvelope<T>
{
    public int? Code { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
}

internal sealed class LoginData
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }
}

internal sealed class UserProfile
{
    public string? Username { get; set; }
    public string? Email { get; set; }
    public double? Balance { get; set; }

    public string? DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Username))
            {
                return Username.Trim();
            }

            return string.IsNullOrWhiteSpace(Email) ? null : Email.Trim();
        }
    }
}

internal sealed class Subscription
{
    public int? Id { get; set; }

    [JsonPropertyName("starts_at")]
    public string? StartsAt { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    public string? Status { get; set; }

    [JsonPropertyName("daily_window_start")]
    public string? DailyWindowStart { get; set; }

    [JsonPropertyName("weekly_window_start")]
    public string? WeeklyWindowStart { get; set; }

    [JsonPropertyName("monthly_window_start")]
    public string? MonthlyWindowStart { get; set; }

    [JsonPropertyName("daily_usage_usd")]
    public double? DailyUsageUsd { get; set; }

    [JsonPropertyName("weekly_usage_usd")]
    public double? WeeklyUsageUsd { get; set; }

    [JsonPropertyName("monthly_usage_usd")]
    public double? MonthlyUsageUsd { get; set; }

    public Group? Group { get; set; }
}

internal sealed class Group
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Platform { get; set; }

    [JsonPropertyName("daily_limit_usd")]
    public double? DailyLimitUsd { get; set; }

    [JsonPropertyName("weekly_limit_usd")]
    public double? WeeklyLimitUsd { get; set; }

    [JsonPropertyName("monthly_limit_usd")]
    public double? MonthlyLimitUsd { get; set; }
}

internal sealed record QuotaMetric(string Label, double Used, double Limit, string? WindowStart, int Hours)
{
    public double Ratio => Limit > 0 ? Math.Clamp(Used / Limit, 0, 1) : 0;
}

internal sealed record SubscriptionSummary(
    Subscription Subscription,
    QuotaMetric? Metric,
    string? UserDisplayName,
    double? Balance,
    DateTimeOffset FetchedAt);

internal enum FetchStateKind
{
    Idle,
    Loading,
    Loaded,
    Empty,
    Failed
}

internal sealed record FetchState(
    FetchStateKind Kind,
    SubscriptionSummary? Summary = null,
    string? Message = null,
    DateTimeOffset? FetchedAt = null,
    double? Balance = null,
    string? UserDisplayName = null)
{
    public static FetchState Idle() => new(FetchStateKind.Idle);

    public static FetchState Loading() => new(FetchStateKind.Loading);

    public static FetchState Loaded(SubscriptionSummary summary) => new(FetchStateKind.Loaded, Summary: summary);

    public static FetchState Empty(DateTimeOffset fetchedAt, double? balance, string? userDisplayName) =>
        new(FetchStateKind.Empty, FetchedAt: fetchedAt, Balance: balance, UserDisplayName: userDisplayName);

    public static FetchState Failed(string message, DateTimeOffset? fetchedAt) =>
        new(FetchStateKind.Failed, Message: message, FetchedAt: fetchedAt);
}
