using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AllinAIQuotaTray;

internal interface ICredentialStore
{
    string? Get(string account);
    void Set(string account, string password);
    void Delete(string account);
    bool Contains(string account);
}

internal static class LocalFiles
{
    public static void Write<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(file, value, AppDefaults.Json);
                file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static T? Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppDefaults.Json);
}

internal sealed class LocalStorage(string directory, ICredentialStore credentials)
{
    public string DirectoryPath { get; } = directory;
    public ICredentialStore Credentials { get; } = credentials;
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string TokenPath => Path.Combine(DirectoryPath, "tokens.json");
    private Config? _config;

    public Config LoadConfig()
    {
        if (_config is not null) return _config;
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(ConfigPath)) { SaveConfig(new Config()); return _config!; }
        var config = LocalFiles.Read<Config>(ConfigPath) ?? throw ClientError.InvalidResponse();
        var normalized = new Config(config.Email?.Trim() ?? "",
            AppDefaults.Intervals.Contains(config.RefreshIntervalSeconds) ? config.RefreshIntervalSeconds : 300);
        using var legacy = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var passwordProperty = legacy.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("password", StringComparison.OrdinalIgnoreCase));
        if (passwordProperty.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(passwordProperty.Value.GetString())
            && normalized.Email.Length > 0)
            Credentials.Set(normalized.Email, passwordProperty.Value.GetString()!);
        if (passwordProperty.Value.ValueKind != JsonValueKind.Undefined || normalized != config) SaveConfig(normalized);
        _config = normalized;
        return normalized;
    }

    public void SaveConfig(Config config) { LocalFiles.Write(ConfigPath, config); _config = config; }
    public TokenCache? LoadTokens(string account)
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var tokens = LocalFiles.Read<TokenCache>(TokenPath);
            return tokens is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken)
                && (tokens.Account is null || string.Equals(tokens.Account, account, StringComparison.OrdinalIgnoreCase)) ? tokens : null;
        }
        catch (JsonException) { return null; }
    }
    public void SaveTokens(TokenCache tokens) => LocalFiles.Write(TokenPath, tokens);
    public void ClearTokens() { if (File.Exists(TokenPath)) File.Delete(TokenPath); }
}

internal sealed class Preferences
{
    public int SchemaVersion { get; set; } = 1;
    public bool ExplicitlySignedOut { get; set; }
    public bool RemindersEnabled { get; set; }
    public double LowBalanceThreshold { get; set; }
    public Dictionary<string, DateTimeOffset> Notices { get; set; } = [];
    public bool AutomaticUpdateChecks { get; set; }
    public DateTimeOffset? LastUpdateAttempt { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public DateTimeOffset? UpdateRetryAt { get; set; }
}

internal sealed class PreferenceStore
{
    private readonly string _path;
    public Preferences Value { get; }
    public PreferenceStore(string directory)
    {
        _path = Path.Combine(directory, "preferences.json");
        Value = File.Exists(_path) ? LocalFiles.Read<Preferences>(_path) ?? throw ClientError.InvalidResponse() : new();
        Value.Notices ??= [];
        if (!new double[] { 0, 1, 5, 10 }.Contains(Value.LowBalanceThreshold)) Value.LowBalanceThreshold = 0;
    }
    public void Save() => LocalFiles.Write(_path, Value);
}

internal static class TimeZoneNames
{
    public static string LocalIanaName()
    {
        var zone = TimeZoneInfo.Local;
        if (zone.Id is "UTC" or "Etc/UTC") return "Etc/UTC";
        if (zone.HasIanaId) return zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) return iana;
        throw new ClientError(FailureKind.Rejected, "无法识别系统时区，请检查 Windows 时区设置。");
    }
}

internal static class RetryPolicy
{
    public static DateTimeOffset? ReadRetryAt(HttpResponseMessage response, DateTimeOffset now)
    {
        try
        {
            var retry = response.Headers.RetryAfter;
            if (retry?.Date is { } date) return date > now ? date : now;
            if (retry?.Delta is { } delta) return now.AddSeconds(Math.Clamp(delta.TotalSeconds, 0, 86400 * 365));
        }
        catch (FormatException) { }
        return null;
    }
}

// State changes and token persistence share this lock; no lock is held across network awaits.
internal sealed class AllinClient : IDisposable
{
    private readonly object _gate = new();
    private readonly LocalStorage _storage;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _now;
    private Config _config = new();
    private TokenCache? _tokens;
    private int _epoch;
    private CancellationTokenSource _session = new();
    private Task<TokenCache>? _renewal;

    public AllinClient(LocalStorage storage, HttpClient? http = null, Func<DateTimeOffset>? now = null)
    {
        _storage = storage;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = http is null;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public (Config Config, bool Configured) Configuration()
    {
        lock (_gate)
        {
            _config = _storage.LoadConfig();
            _tokens = _storage.LoadTokens(_config.Email);
            return (_config, _config.Email.Length > 0 && (_tokens is not null || _storage.Credentials.Contains(_config.Email)));
        }
    }
    public Config SetInterval(int seconds)
    {
        if (!AppDefaults.Intervals.Contains(seconds)) throw new ClientError(FailureKind.Rejected, "请选择有效的刷新间隔。");
        lock (_gate)
        {
            var config = _storage.LoadConfig() with { RefreshIntervalSeconds = seconds };
            _storage.SaveConfig(config);
            return _config = config;
        }
    }

    public async Task<Config> SignInAsync(string email, string password, CancellationToken cancellation = default)
    {
        var expected = NewSession();
        email = email.Trim();
        if (email.Length == 0 || password.Length == 0) throw new ClientError(FailureKind.Rejected, "请输入邮箱与密码。");
        var data = await RequestAsync<LoginData>("auth/login", null, new { email, password }, cancellation).ConfigureAwait(false);
        var tokens = MakeTokens(data, email);
        lock (_gate)
        {
            Check(expected, cancellation);
            // Persist only after the server accepted the sign-in, preserving the previous identity on rejection.
            _storage.Credentials.Set(email, password);
            var config = _storage.LoadConfig() with { Email = email };
            _storage.SaveConfig(config);
            _storage.SaveTokens(tokens);
            _tokens = tokens;
            return _config = config;
        }
    }

    public void Logout()
    {
        NewSession();
        lock (_gate)
        {
            _tokens = null;
            // Clear tokens even when removing the password fails.
            _storage.ClearTokens();
            if (_config.Email.Length > 0) _storage.Credentials.Delete(_config.Email);
        }
    }

    private int NewSession()
    {
        CancellationTokenSource previous;
        int expected;
        lock (_gate)
        {
            previous = _session;
            _session = new();
            _renewal = null;
            expected = ++_epoch;
        }
        previous.Cancel();
        previous.Dispose();
        return expected;
    }
    private void Check(int expected, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_epoch != expected) throw new OperationCanceledException();
    }

    public async Task<AccountResult> FetchAccountStatusAsync(CancellationToken cancellation = default)
    {
        int expected;
        lock (_gate) expected = _epoch;
        var token = await AccessTokenAsync(expected, null, cancellation).ConfigureAwait(false);
        var subscriptions = FetchAsync<List<Subscription>>("subscriptions", token, cancellation);
        var profile = FetchAsync<UserProfile>("user/profile", token, cancellation);
        await Task.WhenAll(subscriptions, profile).ConfigureAwait(false);
        var first = new AccountResult(await subscriptions, await profile);
        lock (_gate) Check(expected, cancellation);
        var retrySubscriptions = first.Subscriptions.Error?.Kind == FailureKind.Authentication;
        var retryProfile = first.Profile.Error?.Kind == FailureKind.Authentication;
        if (!retrySubscriptions && !retryProfile) return first;
        string fresh;
        try { fresh = await AccessTokenAsync(expected, token, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            lock (_gate) Check(expected, cancellation);
            return new(retrySubscriptions ? ResourceResult<List<Subscription>>.Failed(ClientError.From(error)) : first.Subscriptions,
                retryProfile ? ResourceResult<UserProfile>.Failed(ClientError.From(error)) : first.Profile);
        }
        var secondSubscriptions = retrySubscriptions ? FetchAsync<List<Subscription>>("subscriptions", fresh, cancellation) : Task.FromResult(first.Subscriptions);
        var secondProfile = retryProfile ? FetchAsync<UserProfile>("user/profile", fresh, cancellation) : Task.FromResult(first.Profile);
        await Task.WhenAll(secondSubscriptions, secondProfile).ConfigureAwait(false);
        lock (_gate) Check(expected, cancellation);
        return new(await secondSubscriptions, await secondProfile);
    }

    private async Task<string> AccessTokenAsync(int expected, string? rejected, CancellationToken cancellation)
    {
        Task<TokenCache> renewal;
        lock (_gate)
        {
            Check(expected, cancellation);
            if (_tokens is { } tokens && tokens.AccessToken != rejected && tokens.ExpiresAt > _now().ToUnixTimeSeconds() + 60)
                return tokens.AccessToken;
            // One session owns renewal. Cancelling a single observer cannot start a second renewal.
            if (_renewal is null || _renewal.IsCompleted) _renewal = RenewAsync(expected, _session.Token);
            renewal = _renewal;
        }
        var cache = await renewal.WaitAsync(cancellation).ConfigureAwait(false);
        lock (_gate) Check(expected, cancellation);
        return cache.AccessToken;
    }

    private async Task<TokenCache> RenewAsync(int expected, CancellationToken cancellation)
    {
        Config config;
        TokenCache? previous;
        lock (_gate) { config = _config; previous = _tokens ?? _storage.LoadTokens(config.Email); }
        if (config.Email.Length == 0) throw ClientError.MissingCredentials();
        LoginData data;
        if (!string.IsNullOrEmpty(previous?.RefreshToken))
        {
            try { data = await RequestAsync<LoginData>("auth/refresh", null, new { refresh_token = previous.RefreshToken }, cancellation).ConfigureAwait(false); }
            catch (ClientError error) when (error.Kind == FailureKind.Authentication)
            { data = await PasswordLoginAsync(config, cancellation).ConfigureAwait(false); }
        }
        else data = await PasswordLoginAsync(config, cancellation).ConfigureAwait(false);
        var cache = MakeTokens(data, config.Email);
        if (data.RefreshToken is null) cache = cache with { RefreshToken = previous?.RefreshToken ?? "" };
        lock (_gate)
        {
            Check(expected, cancellation);
            _storage.SaveTokens(cache);
            return _tokens = cache;
        }
    }

    private Task<LoginData> PasswordLoginAsync(Config config, CancellationToken cancellation)
    {
        string? password;
        lock (_gate) password = _storage.Credentials.Get(config.Email);
        if (string.IsNullOrEmpty(password)) throw ClientError.MissingCredentials();
        return RequestAsync<LoginData>("auth/login", null, new { email = config.Email, password }, cancellation);
    }
    private TokenCache MakeTokens(LoginData data, string account)
    {
        if (string.IsNullOrWhiteSpace(data.AccessToken) || (data.ExpiresIn ?? 3600) <= 0) throw ClientError.InvalidResponse();
        return new(data.AccessToken, data.RefreshToken ?? "", _now().ToUnixTimeSeconds() + (data.ExpiresIn ?? 3600), account);
    }

    private async Task<ResourceResult<T>> FetchAsync<T>(string path, string token, CancellationToken cancellation) where T : class
    {
        try { return ResourceResult<T>.Success(await RequestAsync<T>(path, token, null, cancellation).ConfigureAwait(false), _now()); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellation.IsCancellationRequested)
        { return ResourceResult<T>.Failed(ClientError.From(error)); }
    }

    private async Task<T> RequestAsync<T>(string path, string? token, object? body, CancellationToken cancellation)
    {
        var suffix = path == "subscriptions" ? "?timezone=" + Uri.EscapeDataString(TimeZoneNames.LocalIanaName()) : "";
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            "https://allincode.top/api/v1/" + path + suffix);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, AppDefaults.Json), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw ClientError.Authentication();
        if ((int)response.StatusCode == 429) throw new ClientError(FailureKind.RateLimited,
            "请求过于频繁，稍后会自动重试。", RetryPolicy.ReadRetryAt(response, _now()) ?? _now().AddMinutes(1));
        if ((int)response.StatusCode >= 500) throw new ClientError(FailureKind.Server, $"服务暂不可用（{(int)response.StatusCode}），稍后会自动重试。");
        if (!response.IsSuccessStatusCode) throw new ClientError(FailureKind.Rejected,
            response.StatusCode == HttpStatusCode.Forbidden ? "当前账号无权访问此功能。" : $"请求失败（{(int)response.StatusCode}），请稍后重试。");
        ApiEnvelope<T>? envelope;
        try { envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), AppDefaults.Json); }
        catch (JsonException) { throw ClientError.InvalidResponse(); }
        if (envelope?.Code is null) throw ClientError.InvalidResponse();
        if (envelope.Code == 401) throw ClientError.Authentication();
        if (envelope.Code != 0) throw new ClientError(FailureKind.Rejected, Format.Shorten(envelope.Message ?? "服务拒绝了此次请求。", 180));
        if (envelope.Data is null) throw ClientError.InvalidResponse();
        return envelope.Data;
    }

    public void Dispose()
    {
        NewSession();
        _session.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
