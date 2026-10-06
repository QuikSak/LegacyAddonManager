using System.Text.Json;
using LegacyAddonManager.Core;

internal static class RcloneTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("rclone remote validation excludes inline configs, traversal and drive paths", Validation);
        yield return ("rclone publish and fetch transfer one verified profile and preserve cached state", Roundtrip);
        yield return ("rclone fetch failure and corrupt downloads never use or replace stale cache", FailedFetch);
        yield return ("rclone detects remote changes and foreign publisher before uploading", Conflicts);
        yield return ("rclone upload failure and verification mismatch report failure", FailedUpload);
        yield return ("rclone process runner preserves literal arguments and clears ambient rclone flags", Arguments);
        yield return ("rclone process runner drains bounded stdout/stderr without deadlock", Output);
        yield return ("rclone process runner kills cancelled and timed-out processes", Cancellation);
    }
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task<Exception> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
    private static SyncProfile Profile(Guid? publisher = null) => new() { PublisherId = publisher ?? Guid.NewGuid() };
    private static byte[] Bytes(SyncProfile profile) => JsonSerializer.SerializeToUtf8Bytes(profile, SettingsStore.JsonOptions);
    private static RcloneProfileTransport Transport(Fixture f, FakeRunner runner) => new(runner, Path.Combine(f.Root, "rclone config.conf"), "cloud:WoW profiles/addons.json", Path.Combine(f.Root, "cache"));

    private sealed class FakeRunner : IRcloneRunner
    {
        public byte[]? Remote { get; set; }
        public int Failure { get; set; }
        public bool CorruptUpload { get; set; }
        public bool FailUpload { get; set; }
        public int Uploads { get; private set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<RcloneResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(arguments);
            if (Failure != 0) return Task.FromResult(new RcloneResult(Failure, "", "https://provider.invalid?token=secret-that-must-not-be-shown"));
            if (arguments[0] == "listremotes") return Task.FromResult(new RcloneResult(0, "cloud:\nstore with spaces:\n:local:\n", ""));
            Assert(arguments[0] == "copyto" && arguments.Contains("--ignore-times"), "Only explicit single-file transfers should run.");
            if (arguments[1].StartsWith("cloud:", StringComparison.Ordinal))
            {
                if (Remote == null) return Task.FromResult(new RcloneResult(4, "", "missing"));
                File.WriteAllBytes(arguments[2], Remote);
            }
            else
            {
                if (FailUpload) return Task.FromResult(new RcloneResult(5, "", "failed"));
                Uploads++;
                Remote = CorruptUpload ? Bytes(Profile()) : File.ReadAllBytes(arguments[1]);
            }
            return Task.FromResult(new RcloneResult(0, "", ""));
        }
    }
    private static Task Validation()
    {
        foreach (var path in new[] { "cloud:WoW/addons.json", "cloud:/profiles/addons.json", "store with spaces:path with spaces/file.json" })
            RcloneProfileTransport.ValidateRemotePath(path);
        foreach (var path in new[] { "C:\\WoW\\addons.json", ":local:profile.json", "cloud,token=secret:profile.json", "-cloud:profile.json", "x:profile.json", "cloud:../profile.json", "cloud:WoW/../profile.json", "cloud:WoW\\profile.json", "cloud:", "cloud:folder", "cloud:path\n/profile.json", "cloud:path:other.json" })
            Throws<ArgumentException>(() => RcloneProfileTransport.ValidateRemotePath(path));
        return Task.CompletedTask;
    }
    private static async Task Roundtrip()
    {
        using var f = new Fixture(); var runner = new FakeRunner(); var transport = Transport(f, runner);
        Assert(await transport.ReadAsync(true, default) == null && !File.Exists(transport.LocalStore.FilePath));
        var profile = Profile(); await transport.PublishAsync(profile, null, default);
        Assert(runner.Uploads == 1 && transport.LocalStore.Read().Profile.PublisherId == profile.PublisherId);
        var snapshot = await transport.ReadAsync(false, default);
        Assert(snapshot!.Profile.Revision == profile.Revision);
        var remotes = await RcloneProfileTransport.ListRemotesAsync(runner, default);
        Assert(remotes.SequenceEqual(new[] { "cloud:", "store with spaces:" }));
        Assert(!Directory.EnumerateDirectories(Path.GetDirectoryName(transport.LocalStore.FilePath)!, ".lam-*").Any());
    }
    private static async Task FailedFetch()
    {
        using var f = new Fixture(); var runner = new FakeRunner { Remote = Bytes(Profile()) }; var transport = Transport(f, runner);
        var snapshot = (await transport.ReadAsync(false, default))!;
        runner.Failure = 5;
        var failure = await ThrowsAsync<IOException>(() => transport.ReadAsync(false, default));
        Assert(!failure.Message.Contains("secret-that-must-not-be-shown") && snapshot.FileHash == transport.LocalStore.Read().FileHash);
        runner.Failure = 0; runner.Remote = System.Text.Encoding.UTF8.GetBytes("broken");
        await ThrowsAsync<InvalidDataException>(() => transport.ReadAsync(false, default));
        Assert(snapshot.FileHash == transport.LocalStore.Read().FileHash);
        runner.Remote = null;
        await ThrowsAsync<IOException>(() => transport.ReadAsync(false, default));
        Assert(snapshot.FileHash == transport.LocalStore.Read().FileHash);
    }
    private static async Task Conflicts()
    {
        using var f = new Fixture(); var profile = Profile(); var runner = new FakeRunner { Remote = Bytes(profile) }; var transport = Transport(f, runner);
        var before = (await transport.ReadAsync(false, default))!;
        runner.Remote = Bytes(Profile(profile.PublisherId));
        await ThrowsAsync<IOException>(() => transport.PublishAsync(profile, before.FileHash, default));
        Assert(runner.Uploads == 0);
        var current = (await transport.ReadAsync(false, default))!;
        await ThrowsAsync<IOException>(() => transport.PublishAsync(Profile(), current.FileHash, default));
        Assert(runner.Uploads == 0 && transport.LocalStore.Read().FileHash == current.FileHash);
    }
    private static async Task FailedUpload()
    {
        using var f = new Fixture(); var runner = new FakeRunner { CorruptUpload = true }; var transport = Transport(f, runner);
        await ThrowsAsync<IOException>(() => transport.PublishAsync(Profile(), null, default));
        Assert(!File.Exists(transport.LocalStore.FilePath), "An unverified upload must not become the cached published profile.");
        runner.CorruptUpload = false; runner.Remote = null; runner.FailUpload = true;
        await ThrowsAsync<IOException>(() => transport.PublishAsync(Profile(), null, default));
        Assert(runner.Remote == null && !File.Exists(transport.LocalStore.FilePath), "Failed upload must preserve remote and local state.");
    }
    private static RcloneProcessRunner ProcessRunner(Fixture f, TimeSpan? timeout = null)
    {
        var config = Path.Combine(f.Root, "config with spaces.conf"); File.WriteAllText(config, "stub");
        return new(Environment.ProcessPath!, config, "dummy password", timeout);
    }
    private static async Task Arguments()
    {
        using var f = new Fixture(); var runner = ProcessRunner(f);
        var previous = Environment.GetEnvironmentVariable("RCLONE_RC"); Environment.SetEnvironmentVariable("RCLONE_RC", "true");
        try
        {
            var result = await runner.RunAsync(new[] { "rclone-test-args", "cloud:space folder/a.json", "literal\"quote", "&;$(no-shell)" }, default);
            Assert(result.ExitCode == 0 && result.Output.Contains("cloud:space folder/a.json|literal\"quote|&;$(no-shell)") && result.Output.EndsWith("|rc="));
            Assert(result.Output.Contains("--config|" + Path.Combine(f.Root, "config with spaces.conf")));
        }
        finally { Environment.SetEnvironmentVariable("RCLONE_RC", previous); }
    }
    private static async Task Output()
    {
        using var f = new Fixture(); var result = await ProcessRunner(f).RunAsync(new[] { "rclone-test-output" }, default);
        Assert(result.ExitCode == 7 && result.Output.Length == 65536 && result.Error.Length == 65536);
    }
    private static async Task Cancellation()
    {
        using var f = new Fixture();
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await ThrowsAsync<OperationCanceledException>(() => ProcessRunner(f).RunAsync(new[] { "rclone-test-wait" }, ct.Token));
        await ThrowsAsync<TimeoutException>(() => ProcessRunner(f, TimeSpan.FromMilliseconds(500)).RunAsync(new[] { "rclone-test-wait" }, default));
    }
}
