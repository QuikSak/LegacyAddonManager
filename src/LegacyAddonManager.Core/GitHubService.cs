using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LegacyAddonManager.Core;

public sealed class GitHubRateLimitException(string message) : Exception(message);

public sealed class GitHubService : IDisposable
{
    private readonly HttpClient client;
    private DateTimeOffset blockedUntil;
    public string Token { get; set; } = "";
    public GitHubService(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<RepositoryVersion> GetLatestAsync(GitHubRepository repository, CancellationToken ct)
    {
        var prefix = $"https://api.github.com/repos/{repository.Slug}";
        using var release = await GetJsonAsync(prefix + "/releases/latest", true, ct);
        if (release != null)
        {
            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("GitHub returned an empty release tag.");
            var options = new List<DownloadOption>();
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    options.Add(new(name, asset.GetProperty("browser_download_url").GetString()!, false));
            }
            options.Add(new("Source archive (tag " + tag + ")", prefix + "/zipball/" + Uri.EscapeDataString(tag), true));
            return new(tag, "release", options);
        }
        using var repo = await GetJsonAsync(prefix, false, ct);
        var branch = repo!.RootElement.GetProperty("default_branch").GetString() ?? throw new InvalidDataException("Repository has no default branch.");
        using var commit = await GetJsonAsync(prefix + "/commits/" + Uri.EscapeDataString(branch), false, ct);
        var sha = commit!.RootElement.GetProperty("sha").GetString() ?? throw new InvalidDataException("Repository has no commits.");
        return new(sha, "commit", [new("Default branch (" + branch + ")", prefix + "/zipball/" + sha, true)]);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, bool allowNotFound, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(url), ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        if (data.Length > 16 * 1024 * 1024) throw new InvalidDataException("GitHub API response is too large.");
        return JsonDocument.Parse(data);
    }

    // Sync resolves the recorded version and asset only. Never fall back to latest or a different ZIP.
    public async Task<(RepositoryVersion Version, DownloadOption Download)> GetPinnedAsync(SyncProfileAddon addon, CancellationToken ct)
    {
        SyncProfileStore.ValidateAddon(addon);
        var prefix = $"https://api.github.com/repos/{GitHubRepository.Parse(addon.RepositoryUrl).Slug}";
        if (addon.VersionKind == "commit")
        {
            var download = new DownloadOption("Source archive (commit)", prefix + "/zipball/" + addon.Version, true);
            return (new(addon.Version, "commit", [download]), download);
        }
        using var release = await GetJsonAsync(prefix + "/releases/tags/" + Uri.EscapeDataString(addon.Version), false, ct);
        if (release!.RootElement.GetProperty("tag_name").GetString() != addon.Version)
            throw new InvalidDataException("GitHub returned a different release than the profile requested.");
        DownloadOption selected;
        if (addon.AssetName == null)
            selected = new("Source archive (tag " + addon.Version + ")", prefix + "/zipball/" + Uri.EscapeDataString(addon.Version), true);
        else
        {
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == addon.AssetName);
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new IOException($"The profile's release ZIP '{addon.AssetName}' is no longer available. No alternative package was selected.");
            selected = new(addon.AssetName, asset.GetProperty("browser_download_url").GetString()!, false);
        }
        return (new(addon.Version, "release", [selected]), selected);
    }

    public async Task DownloadAsync(string url, string destination, IProgress<string>? progress, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(url), ct);
        await EnsureSuccessAsync(response, ct);
        const long maximum = 512L * 1024 * 1024;
        var total = response.Content.Headers.ContentLength;
        if (total > maximum) throw new InvalidDataException("ZIP exceeds the 512 MB download limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        var lastReport = DateTime.MinValue;
        int read;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            received += read;
            if (received > maximum) throw new InvalidDataException("ZIP exceeds the 512 MB download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                progress?.Report(total > 0 ? $"Downloading… {received * 100 / total}%" : $"Downloading… {received / 1024 / 1024} MB");
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < blockedUntil)
            throw new GitHubRateLimitException($"GitHub rate limit reached. Try again after {blockedUntil.ToLocalTime():t}, or add a token in Settings.");
        for (var redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != "https" || !IsGitHubHost(uri.Host) || uri.UserInfo.Length != 0)
                throw new InvalidDataException("GitHub returned an unsupported download address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("LegacyAddonManager/1.4");
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (!string.IsNullOrWhiteSpace(Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new HttpRequestException("GitHub redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many GitHub download redirects.");
    }

    private static bool IsGitHubHost(string host) => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var remainingZero = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0");
        if ((int)response.StatusCode == 429 || (response.StatusCode == HttpStatusCode.Forbidden &&
            (remainingZero || response.Headers.RetryAfter != null || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))))
        {
            blockedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            if (response.Headers.RetryAfter?.Delta is { } delay) blockedUntil = DateTimeOffset.UtcNow + delay;
            else if (response.Headers.RetryAfter?.Date is { } retryDate) blockedUntil = retryDate;
            else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var epoch))
            {
                try { blockedUntil = DateTimeOffset.FromUnixTimeSeconds(epoch).AddSeconds(1); } catch (ArgumentOutOfRangeException) { }
            }
            if (blockedUntil <= DateTimeOffset.UtcNow) blockedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            throw new GitHubRateLimitException($"GitHub rate limit reached. Try again after {blockedUntil.ToLocalTime():t}, or add a token in Settings.");
        }
        throw new HttpRequestException(response.StatusCode switch
        {
            HttpStatusCode.NotFound => "Repository, release, or branch not found. Check the URL and access permissions.",
            HttpStatusCode.Unauthorized => "GitHub rejected the token. Update or clear it in Settings.",
            HttpStatusCode.Forbidden => "GitHub denied access. Check repository permissions or your token.",
            _ => $"GitHub request failed (HTTP {(int)response.StatusCode}). Please try again later."
        });
    }

    public void ResetRateLimit() => blockedUntil = default;
    public void Dispose() => client.Dispose();
}
