using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

public sealed record RcloneResult(int ExitCode, string Output, string Error);
public interface IRcloneRunner
{
    Task<RcloneResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class RcloneProcessRunner(string executable, string configPath, string password = "", TimeSpan? timeout = null) : IRcloneRunner
{
    public async Task<RcloneResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("The bundled rclone executable is unavailable.");
        if (!Path.IsPathFullyQualified(configPath) || !File.Exists(configPath)) throw new IOException("Configure an rclone remote first, or choose an existing rclone config file.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // User-wide rclone flags cannot turn a transfer into a daemon or alter its destination.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        if (password.Length > 0) start.Environment["RCLONE_CONFIG_PASS"] = password;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--config", configPath, "--ask-password=false", "--rc=false", "--log-level", "ERROR", "--stats", "0",
            "--retries", "1", "--low-level-retries", "1", "--contimeout", "15s", "--timeout", "45s", "--max-duration", "90s" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        process.Start();
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw new TimeoutException("rclone timed out. Check the remote connection and try again.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 65536) result.Append(buffer, 0, Math.Min(count, 65536 - result.Length));
        return result.ToString();
    }
}

public sealed class RcloneProfileTransport
{
    private readonly IRcloneRunner runner;
    private readonly string remote;
    private readonly string workRoot;
    public SyncProfileStore LocalStore { get; }

    public RcloneProfileTransport(IRcloneRunner runner, string configPath, string remoteProfile, string cacheRoot)
    {
        ValidateRemotePath(remoteProfile);
        this.runner = runner;
        remote = remoteProfile;
        workRoot = Path.GetFullPath(cacheRoot);
        SafeFiles.EnsureNoLinks(workRoot);
        Directory.CreateDirectory(workRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath) + "\0" + remoteProfile)));
        LocalStore = new(SafeFiles.Child(workRoot, key + ".json"));
    }

    public static void ValidateRemotePath(string remote)
    {
        if (remote == null || remote.Length > 1024 || remote.Any(char.IsControl) || !Regex.IsMatch(remote, @"^[A-Za-z0-9][A-Za-z0-9_. -]{1,99}:[^:]+\.json$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use a configured rclone remote and a profile filename, such as cloud:WoW/legacy-addons.json. Remote names must have at least two characters.");
        var path = remote[(remote.IndexOf(':') + 1)..];
        if (path.Contains('\\') || path.Contains('"') || path.TrimStart('/').Split('/').Any(p => p.Length == 0 || p is "." or ".."))
            throw new ArgumentException("Use forward slashes and a specific JSON filename without . or .. path segments.");
    }

    public static async Task<IReadOnlyList<string>> ListRemotesAsync(IRcloneRunner runner, CancellationToken ct)
    {
        var result = await runner.RunAsync(new[] { "listremotes" }, ct);
        EnsureSuccess(result);
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9_. -]{1,99}:$"))
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProfileSnapshot?> ReadAsync(bool allowMissing, CancellationToken ct)
    {
        var download = await DownloadAsync(allowMissing, ct);
        if (download == null) return null; // A failed/missing download never falls back to a stale cache.
        Cache(download.Value.Bytes);
        return download.Value.Snapshot;
    }

    public async Task PublishAsync(SyncProfile profile, string? expectedHash, CancellationToken ct, IReadOnlyDictionary<string, string>? packages = null)
    {
        SyncProfileStore.Validate(profile);
        var existing = await DownloadAsync(true, ct);
        if (existing?.Snapshot.FileHash != expectedHash)
            throw new IOException("The remote profile changed since preview. Preview and publish again.");
        if (existing != null && existing.Value.Snapshot.Profile.PublisherId != profile.PublisherId)
            throw new IOException("This remote profile belongs to another publisher. Connect as a follower or choose a new remote filename.");
        var work = CreateWork();
        try
        {
            foreach (var hash in profile.Addons.Where(a => a.IsCustom).Select(a => a.ArchiveHash!).Distinct())
            {
                if (packages == null || !packages.TryGetValue(hash, out var zip)) throw new IOException("A selected custom addon package is missing. Rebuild the publication.");
                if (!await Task.Run(() => new FileInfo(zip).Length <= CustomAddonPackages.MaxZipBytes && CustomAddonPackages.HashZip(zip) == hash, ct))
                    throw new InvalidDataException("A custom addon ZIP changed before upload.");
                EnsureSuccess(await runner.RunAsync(new[] { "copyto", zip, ArchiveRemote(hash), "--ignore-times", "--max-transfer", "512M" }, ct));
                // Upload packages first, then commit the profile only after every package verifies.
                await DownloadArchiveAsync(hash, SafeFiles.Child(work, hash + ".zip"), ct);
            }
            var current = await DownloadAsync(true, ct);
            if (current?.Snapshot.FileHash != expectedHash) throw new IOException("The remote profile changed during upload. Preview and publish again.");
            var staged = new SyncProfileStore(Path.Combine(work, "profile.json"));
            staged.Publish(profile, null);
            var expected = staged.Read();
            ct.ThrowIfCancellationRequested();
            EnsureSuccess(await runner.RunAsync(new[] { "copyto", staged.FilePath, remote, "--ignore-times", "--max-transfer", "2M" }, ct));
            var uploaded = await DownloadAsync(false, ct);
            if (uploaded!.Value.Snapshot.FileHash != expected.FileHash)
                throw new IOException("Remote publication could not be verified. Fetch the remote profile before retrying.");
            Cache(uploaded.Value.Bytes);
        }
        finally { SafeFiles.DeleteOwnedWorkDirectory(work, workRoot); }
    }

    public async Task DownloadArchiveAsync(string hash, string destination, CancellationToken ct)
    {
        var source = ArchiveRemote(hash);
        SafeFiles.EnsureNoLinks(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Custom package download requires a new temporary file.");
        var result = await runner.RunAsync(new[] { "copyto", source, destination, "--ignore-times", "--max-transfer", "512M", "--max-depth", "1" }, ct);
        EnsureSuccess(result);
        if (!await Task.Run(() => new FileInfo(destination).Length <= CustomAddonPackages.MaxZipBytes && CustomAddonPackages.HashZip(destination) == hash, ct))
            throw new InvalidDataException("The downloaded custom addon ZIP failed verification. Installed files were preserved.");
    }

    private string ArchiveRemote(string hash)
    {
        if (hash == null || !Regex.IsMatch(hash, "^[A-F0-9]{64}$")) throw new InvalidDataException("Invalid custom package checksum.");
        return remote + ".packages/" + hash + ".zip";
    }

    private async Task<(ProfileSnapshot Snapshot, byte[] Bytes)?> DownloadAsync(bool allowMissing, CancellationToken ct)
    {
        var work = CreateWork();
        try
        {
            var path = Path.Combine(work, "profile.json");
            var result = await runner.RunAsync(new[] { "copyto", remote, path, "--ignore-times", "--max-transfer", "2M", "--max-depth", "1" }, ct);
            if (allowMissing && result.ExitCode is 3 or 4) return null;
            if (result.ExitCode is 3 or 4) throw new IOException("The remote profile does not exist. Publish it from the main computer first.");
            EnsureSuccess(result);
            var snapshot = new SyncProfileStore(path).Read();
            return (snapshot, await File.ReadAllBytesAsync(path, ct));
        }
        finally { SafeFiles.DeleteOwnedWorkDirectory(work, workRoot); }
    }

    private string CreateWork()
    {
        SafeFiles.EnsureNoLinks(workRoot);
        var path = SafeFiles.Child(workRoot, ".lam-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private void Cache(byte[] bytes)
    {
        SafeFiles.EnsureNoLinks(LocalStore.FilePath);
        var temporary = SafeFiles.Child(workRoot, ".lam-cache-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, LocalStore.FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureSuccess(RcloneResult result)
    {
        if (result.ExitCode == 0) return;
        // Provider logs may contain signed URLs or secrets; never surface or persist raw stderr.
        var reason = result.Error.Contains("password", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
            ? "Unable to open the encrypted rclone config. Check its password in rclone Setup."
            : result.ExitCode switch
            {
                2 => "rclone rejected the command or configuration.",
                3 or 4 => "The remote path or profile was not found.",
                5 => "The remote connection failed temporarily. Check your connection and try again.",
                7 => "The storage provider denied access. Check the remote account and permissions.",
                8 => "The transfer exceeded its size limit (2 MB profiles; 512 MB custom ZIPs).",
                10 => "The remote operation timed out.",
                _ => "Check your rclone remote configuration, account access and internet connection."
            };
        throw new IOException($"rclone failed (exit {result.ExitCode}). {reason}");
    }
}
