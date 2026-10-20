namespace AllinAIQuotaTray;

// The host calls this from its UI context; HTTP/session synchronization belongs to AllinClient.
internal sealed class QuotaStore(AllinClient client, PreferenceStore preferences, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private int _generation;
    private int _failureCount;
    private bool _sleeping;
    private CancellationTokenSource? _refreshCancellation;
    public Config Config { get; private set; } = new();
    public AccountSnapshot Snapshot { get; private set; } = new();
    public bool IsStarting { get; private set; } = true;
    public bool IsConfigured { get; private set; }
    public bool IsRefreshing { get; private set; }
    public ClientError? Error { get; private set; }
    public DateTimeOffset? LastAttempt { get; private set; }
    public DateTimeOffset? RetryAt { get; private set; }
    public DateTimeOffset? NextRefreshAt { get; private set; }
    public int? SelectedSubscriptionId { get; set; }
    public event Action? Changed;
    public string DisplayName => Snapshot.Profile?.DisplayName ?? Config.Email;
    public QuotaStatus Status => QuotaPolicy.Evaluate(Snapshot, _now(), SelectedSubscriptionId);
    public bool NeedsLogin => Error?.RequiresLogin == true || Snapshot.SubscriptionError?.RequiresLogin == true || Snapshot.ProfileError?.RequiresLogin == true;
    public bool CanRefresh => IsConfigured && !IsRefreshing && !_sleeping && !NeedsLogin && !(RetryAt > _now());

    public async Task StartAsync()
    {
        try
        {
            var configuration = client.Configuration();
            Config = configuration.Config;
            IsConfigured = configuration.Configured && !preferences.Value.ExplicitlySignedOut;
        }
        catch (Exception error) { Error = ClientError.From(error); }
        IsStarting = false;
        Changed?.Invoke();
        if (IsConfigured) await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        _refreshCancellation?.Dispose();
        _refreshCancellation = new();
        var cancellation = _refreshCancellation.Token;
        var expected = _generation;
        IsRefreshing = true;
        LastAttempt = _now();
        NextRefreshAt = null;
        Changed?.Invoke();
        try
        {
            var result = await client.FetchAccountStatusAsync(cancellation);
            if (expected != _generation || cancellation.IsCancellationRequested) return;
            Snapshot.Apply(result);
            Error = null;
            RetryAt = new[] { result.Subscriptions.Error?.RetryAt, result.Profile.Error?.RetryAt }.Max();
            _failureCount = result.Subscriptions.Value is null && result.Profile.Value is null ? _failureCount + 1 : 0;
        }
        catch (Exception error)
        {
            if (expected != _generation || cancellation.IsCancellationRequested) return;
            Error = ClientError.From(error);
            RetryAt = Error.RetryAt;
            Snapshot.SubscriptionError = Snapshot.ProfileError = Error;
            _failureCount++;
        }
        finally
        {
            if (expected == _generation)
            {
                IsRefreshing = false;
                Schedule();
                Changed?.Invoke();
            }
        }
    }

    public async Task SignInAsync(string email, string password, CancellationToken cancellation = default)
    {
        CancelRefresh();
        var expected = _generation;
        try
        {
            var config = await client.SignInAsync(email, password, cancellation);
            if (expected != _generation) throw new OperationCanceledException();
            Config = config;
            Snapshot = new();
            Error = null;
            _failureCount = 0;
            RetryAt = null;
            IsConfigured = true;
            preferences.Value.ExplicitlySignedOut = false;
            preferences.Save();
            Changed?.Invoke();
            // Authentication finishes independently of quota loading, so the login form can close promptly.
            _ = RefreshAsync();
        }
        finally { Schedule(); Changed?.Invoke(); }
    }

    public void Logout()
    {
        CancelRefresh();
        IsConfigured = false;
        Snapshot = new();
        SelectedSubscriptionId = null;
        Error = null;
        RetryAt = null;
        preferences.Value.ExplicitlySignedOut = true;
        Changed?.Invoke();
        try { preferences.Save(); }
        finally { client.Logout(); }
    }
    public void SetInterval(int seconds) { Config = client.SetInterval(seconds); Schedule(); Changed?.Invoke(); }
    public void SelectSubscription(int id) { SelectedSubscriptionId = id; Changed?.Invoke(); }
    public void Suspend() { _sleeping = true; CancelRefresh(); Changed?.Invoke(); }
    public Task ResumeAsync() { _sleeping = false; return RecoverConnectionAsync(); }
    public Task RecoverConnectionAsync()
    {
        if (!IsConfigured || IsRefreshing || _sleeping || NeedsLogin) return Task.CompletedTask;
        var elapsed = _now() - (LastAttempt ?? DateTimeOffset.MinValue);
        if (elapsed.TotalSeconds >= 60) return RefreshAsync();
        Schedule(TimeSpan.FromSeconds(60) - elapsed);
        return Task.CompletedTask;
    }
    private void CancelRefresh()
    {
        _generation++;
        _refreshCancellation?.Cancel();
        IsRefreshing = false;
        NextRefreshAt = null;
    }
    private void Schedule(TimeSpan? delay = null)
    {
        if (!IsConfigured || _sleeping || NeedsLogin || IsRefreshing) { NextRefreshAt = null; return; }
        var seconds = Math.Min(Config.RefreshIntervalSeconds * Math.Pow(2, Math.Min(_failureCount, 5)), 1800);
        var next = _now() + (delay ?? TimeSpan.FromSeconds(seconds));
        NextRefreshAt = RetryAt is { } retry && retry > next ? retry : next;
    }
}

internal sealed record Notice(string Key, string Title, string Body);

// Rules and deduplication are independent of how Windows chooses to show a notification.
internal sealed class ReminderManager(PreferenceStore preferences)
{
    private string? _account;
    private DateTimeOffset? _subscriptionsChecked;
    private DateTimeOffset? _profileChecked;
    private bool _observedSubscriptions;
    private bool _observedProfile;

    public List<Notice> Evaluate(QuotaStore store, DateTimeOffset now)
    {
        if (store.IsRefreshing || !store.IsConfigured || store.NeedsLogin) return [];
        if (_account != store.Config.Email)
        {
            _account = store.Config.Email;
            _subscriptionsChecked = _profileChecked = null;
            _observedSubscriptions = _observedProfile = false;
        }
        var snapshot = store.Snapshot;
        var freshSubscriptions = snapshot.SubscriptionError is null && snapshot.SubscriptionsFetchedAt is not null
            && snapshot.SubscriptionsFetchedAt != _subscriptionsChecked;
        var freshProfile = snapshot.ProfileError is null && snapshot.ProfileFetchedAt is not null
            && snapshot.ProfileFetchedAt != _profileChecked;
        if (!freshSubscriptions && !freshProfile) return [];
        var notifySubscriptions = _observedSubscriptions;
        var notifyProfile = _observedProfile;
        if (freshSubscriptions) { _observedSubscriptions = true; _subscriptionsChecked = snapshot.SubscriptionsFetchedAt; }
        if (freshProfile) { _observedProfile = true; _profileChecked = snapshot.ProfileFetchedAt; }
        var candidates = new List<(Notice Notice, bool Notify)>();
        var status = QuotaPolicy.Evaluate(snapshot, now, store.SelectedSubscriptionId);
        if (freshSubscriptions && status.Subscription is { } sub)
        {
            var identity = $"{_account}:{sub.Id ?? -1}";
            if (Format.Date(sub.ExpiresAt) is { } expires && expires > now && expires - now <= TimeSpan.FromDays(3))
            {
                var threshold = expires - now <= TimeSpan.FromDays(1) ? 1 : 3;
                candidates.Add((new($"{identity}:expiry:{expires.ToUnixTimeSeconds()}:{threshold}", "订阅即将到期",
                    $"{sub.Group?.Name ?? "订阅"}还有 {Format.Remaining(expires, now)}到期。"), notifySubscriptions));
            }
            if (!status.HasUnknownUsage && status.Primary is { } metric && metric.WindowStart is { } window
                && QuotaPolicy.ResetDate(metric, sub) > now && metric.RemainingRatio <= 0.1)
                candidates.Add((new($"{identity}:quota:{metric.Label}:{window}:10", "订阅额度即将用尽",
                    $"{metric.Label}剩余 {Format.Percent(metric.RemainingRatio)}（${Format.Money(metric.Remaining)}）。"), notifySubscriptions));
        }
        else if (freshProfile && snapshot.SubscriptionError is null && status.Mode is ConsumptionMode.Balance or ConsumptionMode.Unavailable
            && status.Balance is { } balance)
        {
            var threshold = preferences.Value.LowBalanceThreshold;
            var key = $"{_account}:balance:{threshold}";
            if (threshold > 0 && balance <= threshold)
                candidates.Add((new(key, "账户余额较低", $"当前余额 ${Format.Money(balance)}，请留意后续用量。"), notifyProfile));
            else if (balance > threshold) preferences.Value.Notices.Remove(key);
        }
        var settings = preferences.Value;
        var notices = new List<Notice>();
        foreach (var candidate in candidates)
        {
            if (!settings.Notices.TryAdd(candidate.Notice.Key, now)) continue;
            if (settings.RemindersEnabled && candidate.Notify) notices.Add(candidate.Notice);
        }
        foreach (var key in settings.Notices.Where(p => now - p.Value > TimeSpan.FromDays(100)).Select(p => p.Key).ToList())
            settings.Notices.Remove(key);
        preferences.Save();
        return notices;
    }

    public void Retry(Notice notice) { preferences.Value.Notices.Remove(notice.Key); preferences.Save(); }
}
