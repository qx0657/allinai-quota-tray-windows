using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllinAIQuotaTray;

internal static class AppDefaults
{
    public static readonly int[] Intervals = [60, 300, 600, 1800];
    public const string SubscriptionPage = "https://allincode.top/subscriptions";
    public const string Repository = "https://github.com/qx0657/allinai-quota-tray-windows";
    public const string LatestReleaseApi = "https://api.github.com/repos/qx0657/allinai-quota-tray-windows/releases/latest";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

internal sealed record Config(string Email = "", int RefreshIntervalSeconds = 300);
internal sealed record TokenCache(string AccessToken, string RefreshToken, long ExpiresAt, string? Account = null);
internal sealed record ApiEnvelope<T>(int? Code, string? Message, T? Data);
internal sealed record LoginData(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

internal sealed record UserProfile(string? Username = null, string? Email = null, double? Balance = null)
{
    public string? DisplayName => !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : Email?.Trim();
    public double? ValidBalance => Balance is { } value && double.IsFinite(value) ? value : null;
}

internal sealed record Subscription
{
    public int? Id { get; init; }
    [JsonPropertyName("starts_at")] public string? StartsAt { get; init; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; init; }
    public string? Status { get; init; }
    [JsonPropertyName("daily_window_start")] public string? DailyWindowStart { get; init; }
    [JsonPropertyName("weekly_window_start")] public string? WeeklyWindowStart { get; init; }
    [JsonPropertyName("monthly_window_start")] public string? MonthlyWindowStart { get; init; }
    [JsonPropertyName("daily_usage_usd")] public double? DailyUsageUsd { get; init; }
    [JsonPropertyName("weekly_usage_usd")] public double? WeeklyUsageUsd { get; init; }
    [JsonPropertyName("monthly_usage_usd")] public double? MonthlyUsageUsd { get; init; }
    public Group? Group { get; init; }
}

internal sealed record Group
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Platform { get; init; }
    [JsonPropertyName("daily_limit_usd")] public double? DailyLimitUsd { get; init; }
    [JsonPropertyName("weekly_limit_usd")] public double? WeeklyLimitUsd { get; init; }
    [JsonPropertyName("monthly_limit_usd")] public double? MonthlyLimitUsd { get; init; }
}

internal enum FailureKind { Authentication, MissingCredentials, Network, RateLimited, Server, Rejected, InvalidResponse, Storage }

internal sealed class ClientError(FailureKind kind, string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public FailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public bool RequiresLogin => Kind is FailureKind.Authentication or FailureKind.MissingCredentials;
    public static ClientError Authentication() => new(FailureKind.Authentication, "登录已失效，请重新登录。");
    public static ClientError MissingCredentials() => new(FailureKind.MissingCredentials, "请重新登录，或允许访问 Windows 凭据管理器。");
    public static ClientError InvalidResponse() => new(FailureKind.InvalidResponse, "服务返回的数据不完整，请稍后重试。");
    public static ClientError From(Exception error) => error as ClientError ?? error switch
    {
        HttpRequestException => new(FailureKind.Network, "无法连接服务，请检查网络后重试。"),
        OperationCanceledException => new(FailureKind.Network, "请求超时，请稍后重试。"),
        JsonException => InvalidResponse(),
        _ => new(FailureKind.Storage, "本地数据操作失败，请检查文件或凭据管理器权限。")
    };
}

internal sealed record ResourceResult<T>(T? Value, ClientError? Error, DateTimeOffset? FetchedAt) where T : class
{
    public static ResourceResult<T> Success(T value, DateTimeOffset at) => new(value, null, at);
    public static ResourceResult<T> Failed(ClientError error) => new(null, error, null);
}

internal sealed record AccountResult(ResourceResult<List<Subscription>> Subscriptions, ResourceResult<UserProfile> Profile);

internal sealed class AccountSnapshot
{
    public List<Subscription>? Subscriptions { get; private set; }
    public UserProfile? Profile { get; private set; }
    public DateTimeOffset? SubscriptionsFetchedAt { get; private set; }
    public DateTimeOffset? ProfileFetchedAt { get; private set; }
    public ClientError? SubscriptionError { get; set; }
    public ClientError? ProfileError { get; set; }

    public void Apply(AccountResult result)
    {
        if (result.Subscriptions.Value is { } subscriptions)
        {
            Subscriptions = subscriptions;
            SubscriptionsFetchedAt = result.Subscriptions.FetchedAt;
        }
        if (result.Profile.Value is { } profile)
        {
            Profile = profile;
            ProfileFetchedAt = result.Profile.FetchedAt;
        }
        SubscriptionError = result.Subscriptions.Error;
        ProfileError = result.Profile.Error;
    }
}

internal sealed record QuotaMetric(string Label, double Used, double Limit, string? WindowStart, int Hours)
{
    public double RemainingRatio => 1 - Math.Clamp(Used / Limit, 0, 1);
    public double Remaining => Math.Max(0, Limit - Used);
}

internal enum ConsumptionMode { Subscription, Balance, Unavailable, Unknown }
internal sealed record QuotaStatus(ConsumptionMode Mode, Subscription? Subscription, List<QuotaMetric> Metrics,
    QuotaMetric? Primary, double? Balance, bool HasUnknownUsage = false);

internal static class QuotaPolicy
{
    public static List<QuotaMetric> Metrics(Subscription sub)
    {
        (string Label, double? Used, double? Limit, string? Start, int Hours)[] values =
        [
            ("每日", sub.DailyUsageUsd, sub.Group?.DailyLimitUsd, sub.DailyWindowStart, 24),
            ("每周", sub.WeeklyUsageUsd, sub.Group?.WeeklyLimitUsd, sub.WeeklyWindowStart, 168),
            ("每月", sub.MonthlyUsageUsd, sub.Group?.MonthlyLimitUsd, sub.MonthlyWindowStart, 720)
        ];
        return values.Where(v => ValidUsage(v.Used) && ValidLimit(v.Limit))
            .Select(v => new QuotaMetric(v.Label, v.Used!.Value, v.Limit!.Value, v.Start, v.Hours)).ToList();
    }

    private static bool ValidUsage(double? value) => value is { } v && double.IsFinite(v) && v >= 0;
    private static bool ValidLimit(double? value) => value is { } v && double.IsFinite(v) && v > 0;
    public static bool IsActive(Subscription sub, DateTimeOffset now) => sub.Status == "active"
        && !(Format.Date(sub.ExpiresAt) is { } expires && expires <= now)
        && !(Format.Date(sub.StartsAt) is { } starts && starts > now);

    public static QuotaStatus Evaluate(AccountSnapshot snapshot, DateTimeOffset now, int? selectedId = null)
    {
        var subscriptions = snapshot.Subscriptions ?? [];
        var active = subscriptions.Where(s => IsActive(s, now)).OrderBy(s => (long?)s.Id ?? long.MaxValue).ToList();
        var selected = active.FirstOrDefault(s => selectedId.HasValue && s.Id == selectedId) ?? active.FirstOrDefault();
        var balance = snapshot.Profile?.ValidBalance;
        if (selected is not null)
        {
            var metrics = Metrics(selected);
            double?[] limits = [selected.Group?.DailyLimitUsd, selected.Group?.WeeklyLimitUsd, selected.Group?.MonthlyLimitUsd];
            double?[] usage = [selected.DailyUsageUsd, selected.WeeklyUsageUsd, selected.MonthlyUsageUsd];
            var unknown = metrics.Count == 0 || metrics.Count < limits.Count(ValidLimit)
                || usage.Zip(limits).Any(v => v.First is > 0 && !ValidLimit(v.Second));
            return new(ConsumptionMode.Subscription, selected, metrics,
                metrics.OrderByDescending(m => m.Used / m.Limit).FirstOrDefault(), balance, unknown);
        }
        var uncertain = snapshot.Subscriptions is null || subscriptions.Any(s => s.Status != "expired" && !IsActive(s, now));
        var mode = uncertain || balance is null ? ConsumptionMode.Unknown
            : balance > 0 ? ConsumptionMode.Balance : ConsumptionMode.Unavailable;
        return new(mode, null, [], null, balance);
    }

    public static DateTimeOffset? ResetDate(QuotaMetric metric, Subscription sub)
    {
        if (Format.Date(metric.WindowStart) is not { } start) return null;
        var reset = start.AddHours(metric.Hours);
        return Format.Date(sub.ExpiresAt) is { } expires && expires < reset ? expires : reset;
    }
}

internal static class Format
{
    public static string Money(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    public static string Percent(double ratio) => ratio is > 0 and < 0.01 ? "<1%" : ratio is > 0.99 and < 1 ? ">99%"
        : $"{Math.Round(Math.Clamp(ratio, 0, 1) * 100, MidpointRounding.AwayFromZero):0}%";
    public static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    public static string Timestamp(DateTimeOffset date, bool full = false) => date.ToLocalTime().ToString(full ? "yyyy/MM/dd HH:mm" : "HH:mm");
    public static string Remaining(DateTimeOffset until, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (until - now).TotalSeconds);
        if (seconds == 0) return "已到期";
        var minutes = Math.Max(1, (long)(seconds / 60));
        return minutes >= 1440 ? $"{minutes / 1440} 天 {minutes % 1440 / 60} 小时"
            : minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60} 分钟" : $"{minutes} 分钟";
    }
    public static string Updated(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is null) return "尚未成功更新";
        var minutes = Math.Max(0, (int)(now - date.Value).TotalMinutes);
        return minutes < 1 ? "刚刚更新" : minutes < 60 ? $"{minutes} 分钟前更新" : $"上次更新 {Timestamp(date.Value, true)}";
    }
    public static string Status(string? status) => status switch { "active" => "有效", "expired" => "已过期", _ => "状态待确认" };
    public static string Shorten(string value, int max) => value.Length <= max ? value
        : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}

internal enum StatusSymbol { Quota, Balance, Warning, Unknown, Account }

// Presentation is a pure projection: no files, credentials, or network access while opening a menu.
internal sealed class SummaryContent
{
    public string Account { get; set; } = "";
    public string Title { get; set; } = "AllinAI";
    public string Caption { get; set; } = "";
    public string Amount { get; set; } = "--";
    public string? Percentage { get; set; }
    public double? Progress { get; set; }
    public List<string> Lines { get; set; } = [];
    public string? Banner { get; set; }
    public bool IsWarning { get; set; }
    public StatusSymbol Symbol { get; set; } = StatusSymbol.Unknown;
    public string Tooltip => string.Join(" · ", new[] { "AllinAI", Title, $"{Caption} {Amount}", Percentage, Banner }
        .Where(v => !string.IsNullOrWhiteSpace(v)));

    public static SummaryContent Make(QuotaStore store, DateTimeOffset now)
    {
        var content = new SummaryContent { Account = store.DisplayName };
        if (store.IsStarting) { content.Caption = "正在加载"; content.Amount = "请稍候…"; return content; }
        if (!store.IsConfigured)
        {
            content.Title = "欢迎使用 AllinAI";
            content.Caption = "请登录后查看额度与余额";
            content.Amount = "尚未登录";
            content.Symbol = StatusSymbol.Account;
            content.Lines.Add("密码保存在 Windows 凭据管理器。");
            content.Banner = store.Error?.Message;
            return content;
        }
        if (store.NeedsLogin)
        {
            content.Caption = "需要重新登录";
            content.Amount = "登录已失效";
            content.Symbol = StatusSymbol.Account;
            content.Lines.Add("重新登录后继续查看额度与余额。");
            return content;
        }
        var snapshot = store.Snapshot;
        var status = QuotaPolicy.Evaluate(snapshot, now, store.SelectedSubscriptionId);
        switch (status.Mode)
        {
            case ConsumptionMode.Subscription:
                var sub = status.Subscription!;
                content.Title = sub.Group?.Name ?? "订阅额度";
                if (status.Primary is { } metric)
                {
                    content.Caption = $"{metric.Label}剩余额度";
                    content.Amount = $"${Format.Money(metric.Remaining)}";
                    content.Percentage = Format.Percent(metric.RemainingRatio);
                    content.Progress = metric.RemainingRatio;
                    content.Symbol = StatusSymbol.Quota;
                    content.IsWarning = metric.RemainingRatio <= 0.1;
                    content.Lines.Add($"已用 ${Format.Money(metric.Used)} / ${Format.Money(metric.Limit)}");
                    content.Lines.AddRange(status.Metrics.Where(m => m.Label != metric.Label)
                        .Select(m => $"{m.Label}剩余 {Format.Percent(m.RemainingRatio)} · ${Format.Money(m.Remaining)}"));
                    if (QuotaPolicy.ResetDate(metric, sub) is { } reset)
                        content.Lines.Add(reset > now ? $"额度重置：{Format.Remaining(reset, now)}后" : "额度窗口已到，等待刷新确认");
                    if (metric.RemainingRatio == 0) { content.Banner = "此订阅额度已用尽"; content.Symbol = StatusSymbol.Warning; }
                }
                else { content.Caption = "订阅额度"; content.Amount = "暂不可用"; }
                if (status.HasUnknownUsage)
                {
                    content.Lines.Add("部分周期额度暂不可确认");
                    content.Symbol = StatusSymbol.Unknown;
                }
                if (Format.Date(sub.ExpiresAt) is { } expires) content.Lines.Add($"还有 {Format.Remaining(expires, now)}到期");
                content.Lines.Add(status.Balance is { } balance ? $"余额 ${Format.Money(balance)}"
                    + (snapshot.ProfileError is null ? "" : $"（{Format.Updated(snapshot.ProfileFetchedAt, now)}）") : "余额暂不可用");
                content.Lines.Add(Format.Updated(snapshot.SubscriptionsFetchedAt, now));
                break;
            case ConsumptionMode.Balance:
            case ConsumptionMode.Unavailable:
                content.Title = status.Mode == ConsumptionMode.Balance ? "余额消费" : "暂无可用额度";
                content.Caption = "账户余额（USD）";
                content.Amount = $"${Format.Money(status.Balance!.Value)}";
                content.Symbol = status.Mode == ConsumptionMode.Balance ? StatusSymbol.Balance : StatusSymbol.Warning;
                content.IsWarning = status.Mode == ConsumptionMode.Unavailable;
                content.Lines.Add(status.Mode == ConsumptionMode.Balance ? "无有效订阅，可使用账户余额" : "无有效订阅，账户余额不足。");
                content.Lines.Add(Format.Updated(snapshot.ProfileFetchedAt, now));
                break;
            default:
                content.Title = "账户状态";
                content.Caption = "暂时无法确认可用额度";
                if (status.Balance is { } knownBalance) content.Lines.Add($"账户余额 ${Format.Money(knownBalance)}");
                content.Lines.Add(snapshot.Subscriptions is null ? "订阅信息尚未成功获取。" : "订阅状态或有效期需要刷新确认。");
                break;
        }
        var errors = new[] { snapshot.SubscriptionError, snapshot.ProfileError, store.Error }.OfType<ClientError>().ToList();
        if (errors.Count > 0)
        {
            var primaryStale = snapshot.SubscriptionError is not null || store.Error is not null
                || (status.Mode != ConsumptionMode.Subscription && snapshot.ProfileError is not null);
            if (!primaryStale) content.Banner = "余额暂不可用 · 订阅额度已更新";
            else
            {
                var hasData = snapshot.SubscriptionsFetchedAt is not null || snapshot.ProfileFetchedAt is not null;
                content.Banner = hasData ? "刷新失败 · 显示最近数据" : "获取失败 · 可稍后重试";
                content.Symbol = StatusSymbol.Unknown;
                if (status.Mode == ConsumptionMode.Balance) content.Caption = "最近已知余额（USD）";
            }
            content.Lines.Add(errors[0].Message);
        }
        else if (store.IsRefreshing) content.Banner = "正在刷新…";
        return content;
    }
}
