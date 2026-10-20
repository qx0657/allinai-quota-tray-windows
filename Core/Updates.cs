using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AllinAIQuotaTray;

internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? tag, out ReleaseVersion version)
    {
        version = default;
        var parts = (tag?.StartsWith('v') == true ? tag[1..] : tag)?.Split('.');
        if (parts is null || parts.Length != 3) return false;
        var values = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0')
                || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out values[i])) return false;
        version = new(values[0], values[1], values[2]);
        return true;
    }
    public int CompareTo(ReleaseVersion other) => Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    bool Draft = false, bool Prerelease = false)
{
    public string PageUrl => AppDefaults.Repository + "/releases/tag/" + Uri.EscapeDataString(TagName);
}

internal sealed class UpdateError(string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public static UpdateError RateLimited(DateTimeOffset at) => new($"GitHub 请求次数已达上限，请在 {Format.Timestamp(at, true)} 后重试。", at);
}

internal sealed class ReleaseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _currentVersion;
    public ReleaseClient(string currentVersion, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        // No cookies, account authorization, or credentials can flow from AllinClient into GitHub requests.
        _http = http ?? new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _currentVersion = currentVersion;
    }

    public async Task<GitHubRelease?> LatestAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AppDefaults.LatestReleaseApi);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Add("User-Agent", "AllinAI-Quota/" + _currentVersion);
        try
        {
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            var resetHeader = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0");
            if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden
                && (resetHeader || response.Headers.Contains("Retry-After")))
            {
                var now = _now();
                var retry = RetryPolicy.ReadRetryAt(response, now);
                if (retry is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                    && long.TryParse(reset.FirstOrDefault(), out var seconds) && seconds is >= 0 and <= 253402300799)
                    retry = DateTimeOffset.FromUnixTimeSeconds(seconds);
                throw UpdateError.RateLimited(now.AddSeconds(Math.Clamp(((retry ?? now.AddHours(1)) - now).TotalSeconds, 60, 86400)));
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new UpdateError($"GitHub 暂时无法访问（{(int)response.StatusCode}），请稍后重试。");
            var release = JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), AppDefaults.Json);
            if (release is null) throw new UpdateError("GitHub 返回的数据无法识别，请打开更新记录查看。");
            if (release.Draft || release.Prerelease) return null;
            if (!ReleaseVersion.TryParse(release.TagName, out _)) throw new UpdateError("最新 Release 的版本号无法识别；正式版标签应为 v主版本.次版本.修订号。");
            return release;
        }
        catch (HttpRequestException) { throw new UpdateError("无法连接 GitHub，请检查网络后重试，也可打开更新记录。"); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new UpdateError("连接 GitHub 超时，请稍后重试。"); }
        catch (JsonException) { throw new UpdateError("GitHub 返回的数据无法识别，请打开更新记录查看。"); }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

internal enum UpdateStateKind { Idle, Checking, Available, Current, NoRelease, Failed }
internal sealed record UpdateResult(UpdateStateKind Kind, GitHubRelease? Release = null, string? Message = null);

internal sealed class UpdateChecker(ReleaseClient client, PreferenceStore preferences, string currentVersion, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private Task<UpdateResult>? _pending;
    public UpdateResult State { get; private set; } = new(UpdateStateKind.Idle);
    public GitHubRelease? AvailableRelease { get; private set; }
    public bool IsChecking => State.Kind == UpdateStateKind.Checking;
    public event Action? Changed;

    public void SetAutomaticChecks(bool enabled)
    {
        preferences.Value.AutomaticUpdateChecks = enabled;
        preferences.Save();
        Changed?.Invoke();
    }
    public async Task CheckAutomaticallyIfNeededAsync()
    {
        if (!preferences.Value.AutomaticUpdateChecks || _now() - (preferences.Value.LastUpdateAttempt ?? DateTimeOffset.MinValue) < TimeSpan.FromDays(1)) return;
        await CheckAsync();
    }
    public Task<UpdateResult> CheckAsync()
    {
        if (_pending is { IsCompleted: false }) return _pending;
        if (preferences.Value.UpdateRetryAt is { } retry && retry > _now())
        {
            State = new(UpdateStateKind.Failed, Message: UpdateError.RateLimited(retry).Message);
            Changed?.Invoke();
            return Task.FromResult(State);
        }
        preferences.Value.LastUpdateAttempt = _now();
        preferences.Save();
        _cancellation?.Dispose();
        _cancellation = new();
        State = new(UpdateStateKind.Checking);
        Changed?.Invoke();
        return _pending = RunAsync(_generation, _cancellation.Token);
    }

    private async Task<UpdateResult> RunAsync(int expected, CancellationToken cancellation)
    {
        UpdateResult result;
        try
        {
            var release = await client.LatestAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (expected != _generation) throw new OperationCanceledException();
            if (!ReleaseVersion.TryParse(currentVersion, out var current)) throw new UpdateError("当前版本号无法识别，请打开更新记录查看。");
            var newer = release is not null && ReleaseVersion.TryParse(release.TagName, out var latest) && latest.CompareTo(current) > 0;
            AvailableRelease = newer ? release : null;
            result = new(release is null ? UpdateStateKind.NoRelease : newer ? UpdateStateKind.Available : UpdateStateKind.Current, release);
            preferences.Value.LastUpdateCheck = _now();
            preferences.Value.UpdateRetryAt = null;
        }
        catch (OperationCanceledException) { return new(UpdateStateKind.Idle); }
        catch (Exception error)
        {
            if (expected != _generation) return new(UpdateStateKind.Idle);
            if (error is UpdateError { RetryAt: { } retry }) preferences.Value.UpdateRetryAt = retry;
            result = new(UpdateStateKind.Failed, Message: error is UpdateError ? error.Message : "检查更新失败，请稍后重试。");
        }
        if (expected != _generation) return new(UpdateStateKind.Idle);
        State = result;
        preferences.Save();
        Changed?.Invoke();
        return result;
    }

    public void Stop()
    {
        _generation++;
        _cancellation?.Cancel();
        _pending = null;
        if (IsChecking) State = new(UpdateStateKind.Idle);
    }
}
