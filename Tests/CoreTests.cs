using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AllinAIQuotaTray;

internal sealed class MemoryCredentials : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public string? Get(string account) => _values.GetValueOrDefault(account);
    public void Set(string account, string password) => _values[account] = password;
    public void Delete(string account) => _values.TryRemove(account, out _);
    public bool Contains(string account) => _values.ContainsKey(account);
}

internal sealed class MockHttp : HttpMessageHandler
{
    public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Router { get; set; } = null!;
    private readonly ConcurrentDictionary<string, int> _counts = new();
    public string? LastAuthorization { get; private set; }
    public string? LastCookie { get; private set; }
    public Uri? LastUri { get; private set; }
    public int Count(string path) => _counts.GetValueOrDefault(path);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var path = request.RequestUri!.AbsolutePath.Replace("/api/v1/", "");
        var count = _counts.AddOrUpdate(path, 1, (_, previous) => previous + 1);
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastCookie = request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(";", cookie) : null;
        LastUri = request.RequestUri;
        return Router(request, count, cancellation);
    }
    public static HttpResponseMessage Json(object payload, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(payload, AppDefaults.Json), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Data(object data) => Json(new { code = 0, data });
    public static HttpResponseMessage Status(int status) => Json(new { code = status }, status);
}

internal sealed class Rig : IDisposable
{
    public const string Email = "fixture@example.invalid";
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "allinai-tests-" + Guid.NewGuid().ToString("N"));
    public MemoryCredentials Credentials { get; } = new();
    public LocalStorage Storage { get; }
    public PreferenceStore Preferences { get; }
    public MockHttp Http { get; } = new();
    public AllinClient Client { get; }
    public QuotaStore Store { get; }
    private readonly HttpClient _httpClient;
    public Rig(bool expiredToken = false)
    {
        Storage = new(DirectoryPath, Credentials);
        Storage.SaveConfig(new(Email, 300));
        Credentials.Set(Email, Guid.NewGuid().ToString("N"));
        Storage.SaveTokens(new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Now.AddHours(expiredToken ? -1 : 1).ToUnixTimeSeconds(), Email));
        Preferences = new(DirectoryPath);
        Http.Router = (request, count, cancellation) => Task.FromResult(Normal(request));
        _httpClient = new(Http);
        Client = new(Storage, _httpClient, () => Now);
        Client.Configuration();
        Store = new(Client, Preferences, () => Now);
    }
    public Subscription Sub(double? daily = 3.8, double? weekly = 21, string status = "active", int id = 1) => new()
    {
        Id = id, Status = status, StartsAt = Now.AddDays(-10).ToString("O"), ExpiresAt = Now.AddDays(30).ToString("O"),
        DailyWindowStart = Now.AddHours(-1).ToString("O"), WeeklyWindowStart = Now.AddDays(-1).ToString("O"),
        DailyUsageUsd = daily, WeeklyUsageUsd = weekly,
        Group = new() { Name = "Fixture subscription", DailyLimitUsd = 14, WeeklyLimitUsd = 100 }
    };
    public HttpResponseMessage Normal(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/v1/auth/login" or "/api/v1/auth/refresh" => MockHttp.Data(new LoginData(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 3600)),
        "/api/v1/subscriptions" => MockHttp.Data(new[] { Sub() }),
        "/api/v1/user/profile" => MockHttp.Data(new UserProfile("Fixture", Email, 49.99)),
        _ => MockHttp.Status(404)
    };
    public AccountSnapshot Snapshot(List<Subscription> subscriptions, double? balance = 49.99)
    {
        var snapshot = new AccountSnapshot();
        snapshot.Apply(new(ResourceResult<List<Subscription>>.Success(subscriptions, Now), ResourceResult<UserProfile>.Success(new(Balance: balance), Now)));
        return snapshot;
    }
    public void Route(Func<HttpRequestMessage, int, HttpResponseMessage> route) => Http.Router = (request, count, _) => Task.FromResult(route(request, count));
    public void Dispose()
    {
        Store.Suspend();
        Client.Dispose();
        _httpClient.Dispose();
        Directory.Delete(DirectoryPath, true);
    }
}

internal static class CoreTests
{
    private static void Expect(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task ErrorAsync(Func<Task> action, FailureKind kind)
    {
        try { await action(); throw new Exception("Expected a classified error"); }
        catch (ClientError error) { Expect(error.Kind == kind, $"Expected {kind}, got {error.Kind}"); }
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300; i++) { if (condition()) return; await Task.Delay(10); }
        throw new Exception("Timed out waiting for the test request");
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool PathIs(HttpRequestMessage request, string path) => request.RequestUri!.AbsolutePath.EndsWith("/" + path, StringComparison.Ordinal);
    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("Numeric stable release versions", () =>
            {
                Expect(ReleaseVersion.TryParse("v0.10.0", out var newer) && ReleaseVersion.TryParse("0.2.0", out var older) && newer.CompareTo(older) > 0, "Compare components numerically");
                foreach (var invalid in new[] { "v1.0", "v1.0.0-beta", "01.0.0", "1.0.0+build", "1.-1.0", "1.0.99999999999999999", "1.0.０" })
                    Expect(!ReleaseVersion.TryParse(invalid, out _), "Reject unsupported tag " + invalid);
                return Task.CompletedTask;
            }),
            ("Most constrained known quota wins", () =>
            {
                using var rig = new Rig();
                var status = QuotaPolicy.Evaluate(rig.Snapshot([rig.Sub(daily: 2.8, weekly: 90)]), rig.Now);
                Expect(status.Primary?.Label == "每周" && Math.Abs(status.Primary.RemainingRatio - 0.1) < 0.0001, "Weekly remaining 10% beats daily remaining 80%");
                return Task.CompletedTask;
            }),
            ("Expired history yields balance, zero and unknown remain distinct", () =>
            {
                using var rig = new Rig();
                Expect(QuotaPolicy.Evaluate(rig.Snapshot([rig.Sub(status: "expired")]), rig.Now).Mode == ConsumptionMode.Balance, "Expired history must not obscure balance");
                Expect(QuotaPolicy.Evaluate(rig.Snapshot([], 0), rig.Now).Mode == ConsumptionMode.Unavailable, "Zero balance is unavailable");
                Expect(QuotaPolicy.Evaluate(rig.Snapshot([], null), rig.Now).Mode == ConsumptionMode.Unknown, "Missing balance stays unknown");
                Expect(QuotaPolicy.Evaluate(new(), rig.Now).Mode == ConsumptionMode.Unknown, "Missing subscriptions is not an empty list");
                return Task.CompletedTask;
            }),
            ("Missing limits and usage are never zero or unlimited", () =>
            {
                using var rig = new Rig();
                var unknown = QuotaPolicy.Evaluate(rig.Snapshot([rig.Sub(daily: null)]), rig.Now);
                Expect(unknown.HasUnknownUsage && unknown.Metrics.Count == 1, "Missing daily usage is excluded");
                var sub = rig.Sub() with { Group = new() { DailyLimitUsd = null } };
                Expect(QuotaPolicy.Evaluate(rig.Snapshot([sub]), rig.Now).HasUnknownUsage, "Usage without limits is unknown");
                return Task.CompletedTask;
            }),
            ("Exhausted subscriptions do not change billing modes", () =>
            {
                using var rig = new Rig();
                var status = QuotaPolicy.Evaluate(rig.Snapshot([rig.Sub(daily: 20)]), rig.Now);
                Expect(status.Mode == ConsumptionMode.Subscription && status.Primary!.RemainingRatio == 0, "Do not silently switch to balance");
                return Task.CompletedTask;
            }),
            ("Future subscriptions and subscription selection", () =>
            {
                using var rig = new Rig();
                var future = rig.Sub() with { StartsAt = rig.Now.AddHours(1).ToString("O") };
                Expect(QuotaPolicy.Evaluate(rig.Snapshot([future]), rig.Now).Mode == ConsumptionMode.Unknown, "Future subscription is uncertain");
                var noId = rig.Sub() with { Id = null };
                var snapshot = rig.Snapshot([noId, rig.Sub(id: 2), rig.Sub(id: 1)]);
                Expect(QuotaPolicy.Evaluate(snapshot, rig.Now).Subscription!.Id == 1, "Choose a stable known ID first");
                Expect(QuotaPolicy.Evaluate(snapshot, rig.Now, 2).Subscription!.Id == 2, "Allow explicit selection");
                return Task.CompletedTask;
            }),
            ("Quota reset is capped by expiry", () =>
            {
                using var rig = new Rig();
                var sub = rig.Sub() with { ExpiresAt = rig.Now.AddHours(2).ToString("O") };
                Expect(QuotaPolicy.ResetDate(QuotaPolicy.Metrics(sub)[0], sub) == Format.Date(sub.ExpiresAt), "Never claim reset after subscription expires");
                return Task.CompletedTask;
            }),
            ("Partial results preserve independent timestamps", () =>
            {
                using var rig = new Rig();
                var snapshot = rig.Snapshot([]);
                snapshot.Apply(new(ResourceResult<List<Subscription>>.Success([], rig.Now.AddMinutes(5)), ResourceResult<UserProfile>.Failed(new(FailureKind.Server, "fixture"))));
                Expect(snapshot.Profile!.Balance == 49.99 && snapshot.ProfileFetchedAt == rig.Now, "A failure cannot erase or redate successful data");
                return Task.CompletedTask;
            }),
            ("Legacy plaintext credentials migrate and modern config is stable", () =>
            {
                using var rig = new Rig();
                var password = Guid.NewGuid().ToString("N");
                File.WriteAllText(rig.Storage.ConfigPath, JsonSerializer.Serialize(new { email = Rig.Email, password, refreshIntervalSeconds = 300 }));
                var storage = new LocalStorage(rig.DirectoryPath, rig.Credentials);
                storage.LoadConfig();
                Expect(rig.Credentials.Get(Rig.Email) == password, "Migrate into the credential store");
                Expect(!File.ReadAllText(storage.ConfigPath).Contains(password) && !File.ReadAllText(storage.ConfigPath).Contains("password"), "Remove plaintext property");
                File.SetLastWriteTimeUtc(storage.ConfigPath, rig.Now.AddDays(-10).UtcDateTime);
                var modern = new LocalStorage(rig.DirectoryPath, rig.Credentials);
                var before = File.GetLastWriteTimeUtc(storage.ConfigPath);
                modern.LoadConfig(); modern.LoadConfig();
                Expect(File.GetLastWriteTimeUtc(storage.ConfigPath) == before, "No render-time rewrites");
                return Task.CompletedTask;
            }),
            ("Legacy tokens load and mismatched accounts are excluded", () =>
            {
                using var rig = new Rig();
                var cache = rig.Storage.LoadTokens(Rig.Email)!;
                rig.Storage.SaveTokens(cache with { Account = null });
                Expect(rig.Storage.LoadTokens(Rig.Email) is not null, "Keep old Windows token format");
                rig.Storage.SaveTokens(cache with { Account = "other@example.invalid" });
                Expect(rig.Storage.LoadTokens(Rig.Email) is null, "Bind modern tokens to the verified account");
                return Task.CompletedTask;
            }),
            ("Rejected sign-in preserves saved identity", async () =>
            {
                using var rig = new Rig();
                var saved = rig.Credentials.Get(Rig.Email);
                rig.Route((request, _) => PathIs(request, "auth/login") ? MockHttp.Status(401) : rig.Normal(request));
                await ErrorAsync(() => rig.Client.SignInAsync("other@example.invalid", Guid.NewGuid().ToString("N")), FailureKind.Authentication);
                Expect(rig.Storage.LoadConfig().Email == Rig.Email && rig.Credentials.Get(Rig.Email) == saved, "Reject before persistence");
                Expect(!rig.Credentials.Contains("other@example.invalid"), "Do not save unverified credentials");
            }),
            ("Service and forbidden failures never trigger password login", async () =>
            {
                foreach (var status in new[] { 503, 403 })
                {
                    using var rig = new Rig();
                    rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Status(status) : rig.Normal(request));
                    var result = await rig.Client.FetchAccountStatusAsync();
                    Expect(result.Profile.Value!.Balance == 49.99 && result.Subscriptions.Error is not null, "Keep successful resource");
                    Expect(rig.Http.Count("auth/login") == 0 && rig.Http.Count("auth/refresh") == 0, "No reauthentication on non-auth failure");
                }
            }),
            ("Offline and timeout errors are not authentication failures", async () =>
            {
                using var rig = new Rig();
                rig.Http.Router = (_, _, _) => throw new HttpRequestException("fixture offline");
                var result = await rig.Client.FetchAccountStatusAsync();
                Expect(result.Subscriptions.Error?.Kind == FailureKind.Network && rig.Http.Count("auth/login") == 0, "Offline classification");
                rig.Http.Router = (_, _, _) => throw new TaskCanceledException("fixture timeout");
                result = await rig.Client.FetchAccountStatusAsync();
                Expect(result.Profile.Error?.Kind == FailureKind.Network, "Timeout classification");
            }),
            ("Authentication recovery retries only rejected resources", async () =>
            {
                using var rig = new Rig();
                rig.Route((request, count) => PathIs(request, "subscriptions") && count == 1 ? MockHttp.Status(401) : rig.Normal(request));
                var result = await rig.Client.FetchAccountStatusAsync();
                Expect(result.Subscriptions.Value?.Count == 1, "Recover resource");
                Expect(rig.Http.Count("auth/refresh") == 1 && rig.Http.Count("auth/login") == 0, "Renew once");
                Expect(rig.Http.Count("subscriptions") == 2 && rig.Http.Count("user/profile") == 1, "Do not replay successful resource");
            }),
            ("Envelope authentication errors also recover", async () =>
            {
                using var rig = new Rig();
                rig.Route((request, count) => PathIs(request, "subscriptions") && count == 1 ? MockHttp.Json(new { code = 401 }) : rig.Normal(request));
                Expect((await rig.Client.FetchAccountStatusAsync()).Subscriptions.Value is not null && rig.Http.Count("auth/refresh") == 1, "Honor wrapped error codes");
            }),
            ("Concurrent requests share one token renewal", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                var release = Gate();
                rig.Http.Router = async (request, _, _) => { if (PathIs(request, "auth/refresh")) await release.Task; return rig.Normal(request); };
                var first = rig.Client.FetchAccountStatusAsync();
                var second = rig.Client.FetchAccountStatusAsync();
                await UntilAsync(() => rig.Http.Count("auth/refresh") == 1);
                release.SetResult();
                await Task.WhenAll(first, second);
                Expect(rig.Http.Count("auth/refresh") == 1, "Only one renewal in flight");
            }),
            ("Cancelling one observer does not duplicate shared renewal", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                using var cancellation = new CancellationTokenSource();
                var release = Gate();
                rig.Http.Router = async (request, _, _) => { if (PathIs(request, "auth/refresh")) await release.Task; return rig.Normal(request); };
                var cancelled = rig.Client.FetchAccountStatusAsync(cancellation.Token);
                await UntilAsync(() => rig.Http.Count("auth/refresh") == 1);
                cancellation.Cancel();
                try { await cancelled; } catch (OperationCanceledException) { }
                var remaining = rig.Client.FetchAccountStatusAsync();
                release.SetResult(); await remaining;
                Expect(rig.Http.Count("auth/refresh") == 1, "The session, not an individual observer, owns renewal");
            }),
            ("An omitted rotated refresh token preserves the prior token", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                var previous = rig.Storage.LoadTokens(Rig.Email)!.RefreshToken;
                rig.Route((request, _) => PathIs(request, "auth/refresh") ? MockHttp.Data(new LoginData(Guid.NewGuid().ToString("N"), null, 3600)) : rig.Normal(request));
                await rig.Client.FetchAccountStatusAsync();
                Expect(rig.Storage.LoadTokens(Rig.Email)!.RefreshToken == previous, "Do not erase the refresh token");
            }),
            ("Refresh endpoint outage does not trigger password fallback", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                rig.Route((request, _) => PathIs(request, "auth/refresh") ? MockHttp.Status(503) : rig.Normal(request));
                await ErrorAsync(() => rig.Client.FetchAccountStatusAsync(), FailureKind.Server);
                Expect(rig.Http.Count("auth/login") == 0, "Outage is not a rejected session");
            }),
            ("Rejected refresh token permits one password fallback", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                rig.Route((request, _) => PathIs(request, "auth/refresh") ? MockHttp.Status(401) : rig.Normal(request));
                await rig.Client.FetchAccountStatusAsync();
                Expect(rig.Http.Count("auth/login") == 1, "Password fallback only after authentication failure");
            }),
            ("Failed authentication recovery preserves successful profile", async () =>
            {
                using var rig = new Rig();
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Status(401)
                    : PathIs(request, "auth/refresh") ? MockHttp.Status(503) : rig.Normal(request));
                var result = await rig.Client.FetchAccountStatusAsync();
                Expect(result.Profile.Value!.Balance == 49.99 && result.Subscriptions.Error!.Kind == FailureKind.Server, "Keep independent success");
            }),
            ("Missing envelope data remains unknown", async () =>
            {
                using var rig = new Rig();
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Json(new { code = 0 }) : rig.Normal(request));
                var result = await rig.Client.FetchAccountStatusAsync();
                Expect(result.Subscriptions.Value is null && result.Subscriptions.Error!.Kind == FailureKind.InvalidResponse, "Missing data is not an empty subscription list");
            }),
            ("Logout prevents late renewal persistence", async () =>
            {
                using var rig = new Rig(expiredToken: true);
                var release = Gate();
                rig.Http.Router = async (request, _, _) => { if (PathIs(request, "auth/refresh")) await release.Task; return rig.Normal(request); };
                var pending = rig.Client.FetchAccountStatusAsync();
                await UntilAsync(() => rig.Http.Count("auth/refresh") == 1);
                rig.Client.Logout();
                release.SetResult();
                try { await pending; } catch (OperationCanceledException) { }
                Expect(!File.Exists(rig.Storage.TokenPath) && !rig.Credentials.Contains(Rig.Email), "Old session cannot restore deleted tokens");
            }),
            ("Logout rejects a delayed sign-in response", async () =>
            {
                using var rig = new Rig();
                var release = Gate();
                rig.Http.Router = async (request, _, _) => { if (PathIs(request, "auth/login")) await release.Task; return rig.Normal(request); };
                var pending = rig.Client.SignInAsync("other@example.invalid", Guid.NewGuid().ToString("N"));
                await UntilAsync(() => rig.Http.Count("auth/login") == 1);
                rig.Client.Logout(); release.SetResult();
                try { await pending; } catch (OperationCanceledException) { }
                Expect(!File.Exists(rig.Storage.TokenPath) && !rig.Credentials.Contains("other@example.invalid"), "No credential persistence from a superseded sign-in");
                Expect(rig.Storage.LoadConfig().Email == Rig.Email, "Late sign-in cannot change identity");
            }),
            ("Store logout discards late UI results and persists sign-out", async () =>
            {
                using var rig = new Rig();
                var release = Gate();
                rig.Http.Router = async (request, _, _) => { await release.Task; return rig.Normal(request); };
                var start = rig.Store.StartAsync();
                await UntilAsync(() => rig.Http.Count("subscriptions") == 1);
                rig.Store.Logout();
                release.SetResult(); await start;
                Expect(!rig.Store.IsConfigured && rig.Store.Snapshot.Profile is null && rig.Store.Snapshot.Subscriptions is null, "Do not restore signed-out UI");
                Expect(new PreferenceStore(rig.DirectoryPath).Value.ExplicitlySignedOut, "Persist explicit sign-out");
            }),
            ("Manual, network and wake refresh respect rate-limit cooldown", async () =>
            {
                using var rig = new Rig();
                rig.Route((_, _) => { var response = MockHttp.Status(429); response.Headers.Add("Retry-After", "120"); return response; });
                await rig.Store.StartAsync();
                Expect(rig.Store.RetryAt == rig.Now.AddSeconds(120) && !rig.Store.CanRefresh, "Honor server delay");
                await rig.Store.RefreshAsync(); await rig.Store.RecoverConnectionAsync();
                rig.Store.Suspend(); await rig.Store.ResumeAsync();
                Expect(rig.Http.Count("subscriptions") == 1, "All entry points honor cooldown");
                rig.Now = rig.Now.AddSeconds(121);
                await rig.Store.RefreshAsync();
                Expect(rig.Http.Count("subscriptions") == 2, "Allow manual retry after cooldown");
            }),
            ("HTTP-date Retry-After and failure backoff", async () =>
            {
                using var rig = new Rig();
                rig.Route((_, _) => { var response = MockHttp.Status(429); response.Headers.Add("Retry-After", rig.Now.AddMinutes(45).ToString("R")); return response; });
                await rig.Store.StartAsync();
                Expect(rig.Store.NextRefreshAt == rig.Now.AddMinutes(45), "Server cooldown can exceed local backoff");
                using var failed = new Rig();
                failed.Route((_, _) => MockHttp.Status(503));
                await failed.Store.StartAsync();
                Expect(failed.Store.NextRefreshAt == failed.Now.AddSeconds(600), "Back off after complete failure");
                failed.Store.Suspend();
                Expect(failed.Store.NextRefreshAt is null && !failed.Store.CanRefresh, "Pause while sleeping");
            }),
            ("Offline presentation retains known balance with stale marker", async () =>
            {
                using var rig = new Rig();
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Data(Array.Empty<Subscription>()) : rig.Normal(request));
                await rig.Store.StartAsync();
                rig.Http.Router = (_, _, _) => throw new HttpRequestException("fixture offline");
                await rig.Store.RefreshAsync();
                var content = SummaryContent.Make(rig.Store, rig.Now);
                Expect(content.Amount == "$49.99" && content.Caption.Contains("最近已知") && content.Banner!.Contains("最近数据"), "Show stale, not zero");
            }),
            ("Fresh subscription reminders survive profile failure", async () =>
            {
                using var rig = new Rig();
                var sub = rig.Sub() with { ExpiresAt = rig.Now.AddDays(4).ToString("O") };
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Data(new[] { sub }) : rig.Normal(request));
                rig.Preferences.Value.RemindersEnabled = true;
                var reminders = new ReminderManager(rig.Preferences);
                await rig.Store.StartAsync();
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 0, "Seed quietly on startup");
                rig.Now = rig.Now.AddDays(2);
                rig.Route((request, _) => PathIs(request, "user/profile") ? MockHttp.Status(503)
                    : PathIs(request, "subscriptions") ? MockHttp.Data(new[] { sub }) : rig.Normal(request));
                await rig.Store.RefreshAsync();
                var notices = reminders.Evaluate(rig.Store, rig.Now);
                Expect(notices.Count == 1 && notices[0].Key.Contains(":expiry:"), "Evaluate fresh subscriptions independently");
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 0, "Deduplicate the same result");
            }),
            ("Low quota reminders are opt-in and deduplicated by window", async () =>
            {
                using var rig = new Rig();
                var sub = rig.Sub();
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Data(new[] { sub }) : rig.Normal(request));
                rig.Preferences.Value.RemindersEnabled = true;
                var reminders = new ReminderManager(rig.Preferences);
                await rig.Store.StartAsync(); reminders.Evaluate(rig.Store, rig.Now);
                sub = sub with { DailyUsageUsd = 13.5 };
                rig.Now = rig.Now.AddMinutes(5);
                await rig.Store.RefreshAsync();
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 1, "Crossing low quota produces one notice");
                rig.Now = rig.Now.AddMinutes(5); await rig.Store.RefreshAsync();
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 0, "Do not repeat within a window");
            }),
            ("Low balance reminder rearms after recharge", async () =>
            {
                using var rig = new Rig();
                double balance = 2;
                rig.Route((request, _) => PathIs(request, "subscriptions") ? MockHttp.Data(Array.Empty<Subscription>())
                    : PathIs(request, "user/profile") ? MockHttp.Data(new UserProfile(Balance: balance)) : rig.Normal(request));
                rig.Preferences.Value.RemindersEnabled = true;
                rig.Preferences.Value.LowBalanceThreshold = 5;
                var reminders = new ReminderManager(rig.Preferences);
                await rig.Store.StartAsync();
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 0, "No historical warning on startup");
                balance = 9; rig.Now = rig.Now.AddMinutes(5); await rig.Store.RefreshAsync(); reminders.Evaluate(rig.Store, rig.Now);
                balance = 2; rig.Now = rig.Now.AddMinutes(5); await rig.Store.RefreshAsync();
                Expect(reminders.Evaluate(rig.Store, rig.Now).Count == 1, "Notify after dropping below the threshold again");
            }),
            ("GitHub requests are credential-independent and links are trusted", async () =>
            {
                using var rig = new Rig();
                rig.Route((_, _) => MockHttp.Json(new { tag_name = "v0.3.0", html_url = "https://example.invalid/untrusted", draft = false, prerelease = false }));
                using var http = new HttpClient(rig.Http);
                using var client = new ReleaseClient("0.2.0", http, () => rig.Now);
                var release = await client.LatestAsync(default);
                Expect(rig.Http.LastAuthorization is null && rig.Http.LastCookie is null, "No AllinAI authorization or cookies");
                Expect(rig.Http.LastUri!.AbsoluteUri == AppDefaults.LatestReleaseApi, "Check Windows repository");
                Expect(release!.PageUrl == AppDefaults.Repository + "/releases/tag/v0.3.0", "Construct the trusted release URL");
            }),
            ("Update checker never offers downgrades", async () =>
            {
                foreach (var version in new[] { "v0.2.0", "v0.1.0" })
                {
                    using var rig = new Rig();
                    rig.Route((_, _) => MockHttp.Json(new GitHubRelease(version)));
                    using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                    var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                    Expect((await checker.CheckAsync()).Kind == UpdateStateKind.Current && checker.AvailableRelease is null, "No downgrade");
                }
            }),
            ("No release, invalid response and prerelease are distinct", async () =>
            {
                foreach (var kind in new[] { "none", "draft", "prerelease", "invalid", "malformed" })
                {
                    using var rig = new Rig();
                    rig.Route((_, _) => kind switch
                    {
                        "none" => MockHttp.Status(404), "draft" => MockHttp.Json(new GitHubRelease("v0.3.0", Draft: true)),
                        "prerelease" => MockHttp.Json(new GitHubRelease("v0.3.0", Prerelease: true)),
                        "invalid" => MockHttp.Json(new GitHubRelease("main")), _ => MockHttp.Json(new { missing = true })
                    });
                    using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                    var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                    var result = await checker.CheckAsync();
                    Expect(result.Kind == (kind is "invalid" or "malformed" ? UpdateStateKind.Failed : UpdateStateKind.NoRelease), "Classify " + kind);
                }
            }),
            ("Concurrent update clicks share one request", async () =>
            {
                using var rig = new Rig();
                var release = Gate();
                rig.Http.Router = async (_, _, _) => { await release.Task; return MockHttp.Json(new GitHubRelease("v0.3.0")); };
                using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                var first = checker.CheckAsync(); var second = checker.CheckAsync();
                Expect(ReferenceEquals(first, second), "Share pending check");
                release.SetResult(); await Task.WhenAll(first, second);
                Expect(rig.Http.Count("/repos/qx0657/allinai-quota-tray-windows/releases/latest") == 1, "Exactly one GitHub request");
            }),
            ("Automatic updates default off and daily schedule survives restart", async () =>
            {
                using var rig = new Rig();
                rig.Route((_, _) => MockHttp.Json(new GitHubRelease("v0.3.0")));
                using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                await checker.CheckAutomaticallyIfNeededAsync();
                Expect(rig.Http.LastUri is null, "Default off");
                checker.SetAutomaticChecks(true); await checker.CheckAutomaticallyIfNeededAsync();
                Expect(checker.AvailableRelease is not null, "Remember a newer release");
                var path = "/repos/qx0657/allinai-quota-tray-windows/releases/latest";
                rig.Now = rig.Now.AddHours(2);
                var restarted = new UpdateChecker(client, new PreferenceStore(rig.DirectoryPath), "0.2.0", () => rig.Now);
                await restarted.CheckAutomaticallyIfNeededAsync();
                Expect(rig.Http.Count(path) == 1, "Restart cannot reset the daily interval");
                await restarted.CheckAsync();
                Expect(rig.Http.Count(path) == 2, "Manual check bypasses the daily interval");
                rig.Route((_, _) => MockHttp.Status(503));
                rig.Now = rig.Now.AddDays(1);
                await checker.CheckAutomaticallyIfNeededAsync(); await checker.CheckAutomaticallyIfNeededAsync();
                Expect(rig.Http.Count(path) == 3 && checker.AvailableRelease is not null, "Failed attempt counts toward daily interval and keeps a known update");
            }),
            ("GitHub rate-limit cooldown survives restarts", async () =>
            {
                foreach (var header in new[] { "retry", "reset" })
                {
                    using var rig = new Rig();
                    rig.Route((_, _) =>
                    {
                        var response = MockHttp.Status(header == "retry" ? 429 : 403);
                        if (header == "retry") response.Headers.Add("Retry-After", "120");
                        else { response.Headers.Add("X-RateLimit-Remaining", "0"); response.Headers.Add("X-RateLimit-Reset", rig.Now.AddSeconds(120).ToUnixTimeSeconds().ToString()); }
                        return response;
                    });
                    using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                    var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                    Expect((await checker.CheckAsync()).Kind == UpdateStateKind.Failed, "Show a classified update failure");
                    var restartedPreferences = new PreferenceStore(rig.DirectoryPath);
                    Expect(restartedPreferences.Value.UpdateRetryAt == rig.Now.AddSeconds(120), "Persist cooldown");
                    var restarted = new UpdateChecker(client, restartedPreferences, "0.2.0", () => rig.Now);
                    await restarted.CheckAsync();
                    Expect(rig.Http.Count("/repos/qx0657/allinai-quota-tray-windows/releases/latest") == 1, "No repeated request during cooldown");
                }
            }),
            ("Stopping update checks discards late responses", async () =>
            {
                using var rig = new Rig();
                var release = Gate();
                rig.Http.Router = async (_, _, _) => { await release.Task; return MockHttp.Json(new GitHubRelease("v0.3.0")); };
                using var client = new ReleaseClient("0.2.0", new HttpClient(rig.Http), () => rig.Now);
                var checker = new UpdateChecker(client, rig.Preferences, "0.2.0", () => rig.Now);
                var pending = checker.CheckAsync(); checker.Stop(); release.SetResult(); await pending;
                Expect(checker.State.Kind == UpdateStateKind.Idle && checker.AvailableRelease is null && rig.Preferences.Value.LastUpdateCheck is null, "Cancelled response cannot update state");
            }),
            ("IANA timezone conversion uses the platform database", () =>
            {
                Expect(!TimeZoneNames.LocalIanaName().Contains("Standard Time"), "Send an IANA zone, not a Windows identifier");
                Expect(TimeZoneInfo.TryConvertWindowsIdToIanaId("Taipei Standard Time", out var taipei) && taipei == "Asia/Taipei", "Support Taipei without a hand-maintained map");
                return Task.CompletedTask;
            }),
            ("Tooltip truncation preserves UTF-16 boundaries", () =>
            {
                var text = new string('x', 62) + char.ConvertFromUtf32(0x1F600);
                var shortened = Format.Shorten(text, 63);
                Expect(shortened.Length <= 63 && !char.IsHighSurrogate(shortened[^1]), "Do not split surrogate pairs");
                return Task.CompletedTask;
            })
        ];
        var failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error.Message); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
        return failed == 0 ? 0 : 1;
    }
}
