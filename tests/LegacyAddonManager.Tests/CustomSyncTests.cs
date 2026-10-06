using System.IO.Compression;
using System.Text.Json;
using LegacyAddonManager.Core;

internal static class CustomSyncTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Custom publication is opt-in, includes unknown groups, preserves files and strips ownership markers", Export);
        yield return ("Custom ZIP roundtrip preserves actual folder names, binary files and empty directories without GitHub", Roundtrip);
        yield return ("Custom updates change owned folders and block local edits and unmanaged collisions", Protection);
        yield return ("Missing or corrupt custom ZIPs preserve installed files and defer optional removals", Corruption);
        yield return ("Custom upload failure or failed verification preserves the previous profile", PublicationFailure);
        yield return ("Custom profile rejects unsafe identifiers, conflicting folders and incompatible schemas", Validation);
        yield return ("Profile changed during custom download prevents committing addon files", Race);
        yield return ("Custom install save failures and cancellation roll back files and new tracking", Rollback);
        yield return ("Custom extras are preserved by default and explicitly removed without touching unmanaged folders", Extras);
    }
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Same(Dictionary<string, string> before, Dictionary<string, string> after) =>
        Assert(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value), "Files changed unexpectedly.");
    private static void Write(string root, string folder)
    {
        var path = SafeFiles.Child(root, folder); Directory.CreateDirectory(path);
        // The installed folder can differ from the TOC filename; custom sync must preserve it.
        File.WriteAllText(Path.Combine(path, "Different_Wrath.toc"), "## Interface: 30300\n## Title: Custom addon\nmain.lua");
        File.WriteAllText(Path.Combine(path, "main.lua"), "-- custom code");
        File.WriteAllBytes(Path.Combine(path, "texture.blp"), [0, 255, 8, 9, 0]);
        Directory.CreateDirectory(Path.Combine(path, "Empty library"));
    }
    private sealed class Remote : IRcloneRunner
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool FailPackageUpload, CorruptPackageUpload;
        public int PackageDownloads;
        public Action<string>? AfterDownload;
        public Task<RcloneResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert(args[0] == "copyto", "Custom sync must transfer individual files only.");
            if (args[1].StartsWith("cloud:"))
            {
                if (!Files.TryGetValue(args[1], out var bytes)) return Task.FromResult(new RcloneResult(4, "", "missing"));
                File.WriteAllBytes(args[2], bytes);
                if (args[1].EndsWith(".zip")) { PackageDownloads++; AfterDownload?.Invoke(args[1]); }
            }
            else
            {
                if (args[2].EndsWith(".zip") && FailPackageUpload) return Task.FromResult(new RcloneResult(5, "", "upload failure"));
                Files[args[2]] = args[2].EndsWith(".zip") && CorruptPackageUpload ? [1, 2, 3] : File.ReadAllBytes(args[1]);
            }
            return Task.FromResult(new RcloneResult(0, "", ""));
        }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; throw new Exception("Custom addons must not be requested from GitHub."); }
    }
    private sealed class Pair : IDisposable
    {
        public Fixture Publisher { get; } = new();
        public Fixture Follower { get; } = new();
        public Remote Runner { get; } = new();
        public RcloneProfileTransport Upload { get; }
        public RcloneProfileTransport Download { get; }
        public NoNetwork Handler { get; } = new();
        public GitHubService GitHub { get; }
        public Pair()
        {
            Publisher.Settings.Addons.Clear(); Follower.Settings.Addons.Clear();
            Write(Publisher.AddOns, "MyCustom"); Write(Follower.AddOns, "Unrelated");
            Upload = new(Runner, Path.Combine(Publisher.Root, "config.conf"), "cloud:WoW/addons.json", Path.Combine(Publisher.Root, "cache"));
            Download = new(Runner, Path.Combine(Follower.Root, "config.conf"), "cloud:WoW/addons.json", Path.Combine(Follower.Root, "cache"));
            GitHub = new(Handler);
        }
        public ProfileExport Export(params string[] selected) => new ProfileSyncService(Publisher.Store).Export(Publisher.Settings, default, selected,
            Path.Combine(Publisher.Root, "packages-" + Guid.NewGuid().ToString("N")));
        public async Task<ProfileSnapshot> Publish(params string[] selected)
        {
            var export = Export(selected);
            var previous = await Upload.ReadAsync(true, default);
            await Upload.PublishAsync(export.Profile, previous?.FileHash, default, export.Packages);
            return (await Download.ReadAsync(false, default))!;
        }
        public Task<int> Apply(ProfileSnapshot snapshot, bool extras = false, CancellationToken ct = default) =>
            new ProfileSyncService(Follower.Store).ApplyAsync(Follower.Settings, Download.LocalStore, snapshot, extras, GitHub, null, ct, Download);
        public void Dispose() { GitHub.Dispose(); Publisher.Dispose(); Follower.Dispose(); }
    }
    private static Task Export()
    {
        using var p = new Pair();
        var management = new AddonManagement(p.Publisher.Store);
        var unknown = management.AssignSource(p.Publisher.Settings, "MyCustom", "https://github.com/owner/Unknown");
        Write(p.Publisher.AddOns, "Companion"); management.AssignSource(p.Publisher.Settings, "Companion", unknown.RepositoryUrl);
        var before = SafeFiles.HashFiles(p.Publisher.AddOns, "");
        var export = p.Export("MyCustom");
        Assert(export.Profile.SchemaVersion == 2 && export.Profile.Addons.Single().IsCustom && export.Profile.Addons.Single().Folders.Count == 2);
        Assert(p.Publisher.Settings.Addons.Single().InstalledVersion == null, "Publication must not invent a GitHub version or take ownership on the publisher.");
        Same(before, SafeFiles.HashFiles(p.Publisher.AddOns, ""));
        var none = new AppSettings { WoWPath = p.Publisher.Settings.WoWPath };
        Assert(new ProfileSyncService(p.Publisher.Store).Export(none).Profile.Addons.Count == 0, "Unmanaged files must be excluded unless selected.");
        // Manager-installed code can be customized and explicitly published as files.
        using var f = new Fixture(); f.Install("v1", "Known");
        File.WriteAllText(Path.Combine(f.AddOns, "Known", "edited.lua"), "local customization");
        var custom = new ProfileSyncService(f.Store).Export(f.Settings, default, new[] { "Known" }, Path.Combine(f.Root, "custom-package"));
        using var zip = ZipFile.OpenRead(custom.Packages.Single().Value);
        Assert(custom.Profile.Addons.Single().IsCustom && !zip.Entries.Any(e => e.FullName.EndsWith(AddonInstaller.MarkerFile)));
        Assert(zip.Entries.Any(e => e.FullName.EndsWith("edited.lua")));
        var json = JsonSerializer.Serialize(custom.Profile);
        Assert(!json.Contains(f.Root.Replace("\\", "\\\\")) && !json.Contains(f.Addon.InstallationId!), "The portable profile must exclude paths and installation ownership.");
        return Task.CompletedTask;
    }
    private static async Task Roundtrip()
    {
        using var p = new Pair(); var snapshot = await p.Publish("MyCustom");
        Assert(await p.Apply(snapshot) == 1 && p.Handler.Calls == 0);
        var local = p.Follower.Store.Load().Addons.Single();
        Assert(local.IsCustom && local.CustomPublisherId == p.Publisher.Settings.SyncPublisherId && local.Origin == "Custom sync");
        Same(SafeFiles.HashFiles(Path.Combine(p.Publisher.AddOns, "MyCustom"), AddonInstaller.MarkerFile),
            SafeFiles.HashFiles(Path.Combine(p.Follower.AddOns, "MyCustom"), AddonInstaller.MarkerFile));
        var downloads = p.Runner.PackageDownloads;
        Assert(await p.Apply(snapshot) == 0 && p.Runner.PackageDownloads == downloads, "Current custom versions must not download again.");
        Assert(Directory.Exists(Path.Combine(p.Follower.AddOns, "Unrelated")));
        var repeated = p.Export("MyCustom");
        Assert(repeated.Profile.Addons.Single().ArchiveHash == snapshot.Profile.Addons.Single().ArchiveHash, "Unchanged custom files must produce the same remote ZIP name.");
        p.Publisher.Settings.CustomPublishFolders[ManagementPaths.ForWoW(p.Publisher.Settings.WoWPath)!] = ["MyCustom"];
        p.Publisher.Store.Save(p.Publisher.Settings);
        Assert(p.Publisher.Store.Load().CustomPublishFolders.Values.Single().Single() == "MyCustom", "Custom publish choices must persist without changing ownership.");
    }
    private static async Task Protection()
    {
        using var p = new Pair(); var snapshot = await p.Publish("MyCustom"); await p.Apply(snapshot);
        File.WriteAllText(Path.Combine(p.Publisher.AddOns, "MyCustom", "main.lua"), "-- new custom code");
        var update = await p.Publish("MyCustom"); Assert(update.Profile.Addons.Single().ContentHash != snapshot.Profile.Addons.Single().ContentHash);
        await p.Apply(update);
        Assert(File.ReadAllText(Path.Combine(p.Follower.AddOns, "MyCustom", "main.lua")) == "-- new custom code");
        File.WriteAllText(Path.Combine(p.Follower.AddOns, "MyCustom", "local.lua"), "-- follower changes");
        var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        await Throws<IOException>(() => p.Apply(update)); Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
        using var conflict = new Pair(); Write(conflict.Follower.AddOns, "MyCustom");
        var target = await conflict.Publish("MyCustom"); var original = SafeFiles.HashFiles(conflict.Follower.AddOns, "");
        await Throws<IOException>(() => conflict.Apply(target)); Same(original, SafeFiles.HashFiles(conflict.Follower.AddOns, ""));
        Assert(conflict.Follower.Settings.Addons.Count == 0);
    }
    private static async Task Corruption()
    {
        using var p = new Pair(); p.Follower.Settings.Addons.Add(p.Follower.Addon); p.Follower.Install("v1", "Extra");
        var snapshot = await p.Publish("MyCustom");
        var key = p.Runner.Files.Keys.Single(k => k.EndsWith(".zip")); var originalZip = p.Runner.Files[key];
        p.Runner.Files[key] = [1, 2, 3]; var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        await Throws<InvalidDataException>(() => p.Apply(snapshot, true)); Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
        Assert(p.Follower.Settings.Addons.Count == 1 && p.Follower.Settings.Addons.Single().Name == "Example");
        p.Runner.Files.Remove(key); await Throws<IOException>(() => p.Apply(snapshot, true));
        Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
        p.Runner.Files[key] = originalZip; await p.Apply(snapshot, true);
        Assert(!Directory.Exists(Path.Combine(p.Follower.AddOns, "Extra")) && Directory.Exists(Path.Combine(p.Follower.AddOns, "Unrelated")));
    }
    private static async Task PublicationFailure()
    {
        using var p = new Pair(); await p.Publish("MyCustom");
        var before = p.Runner.Files["cloud:WoW/addons.json"].ToArray();
        File.WriteAllText(Path.Combine(p.Publisher.AddOns, "MyCustom", "main.lua"), "new");
        p.Runner.FailPackageUpload = true; await Throws<IOException>(() => p.Publish("MyCustom"));
        Assert(before.SequenceEqual(p.Runner.Files["cloud:WoW/addons.json"]));
        p.Runner.FailPackageUpload = false; p.Runner.CorruptPackageUpload = true;
        await Throws<InvalidDataException>(() => p.Publish("MyCustom"));
        Assert(before.SequenceEqual(p.Runner.Files["cloud:WoW/addons.json"]));
        await Throws<IOException>(() => p.Upload.PublishAsync(p.Export("MyCustom").Profile, p.Upload.LocalStore.Read().FileHash, default));
        Assert(before.SequenceEqual(p.Runner.Files["cloud:WoW/addons.json"]));
    }
    private static async Task Validation()
    {
        using var p = new Pair();
        foreach (var change in new Action<SyncProfile>[]
        {
            s => s.SchemaVersion = 1, s => s.Addons[0].ArchiveHash = "../escape", s => s.Addons[0].CustomSourceId = "cloud:other/file.zip",
            s => s.Addons[0].Folders = [".."], s => s.Addons.Add(s.Addons[0]), s => s.Addons[0].RepositoryUrl = "https://github.com/o/r",
            s => s.Addons[0].Version = "Unknown", s => s.Addons[0].Name = "bad\nname"
        })
        {
            var profile = p.Export("MyCustom").Profile; change(profile);
            await Throws<InvalidDataException>(() => { SyncProfileStore.Validate(profile); return Task.CompletedTask; });
        }
        var normal = p.Export("MyCustom");
        await Throws<InvalidDataException>(() => p.Download.DownloadArchiveAsync("../other", Path.Combine(p.Follower.Root, "bad.zip"), default));
        var missing = p.Export("MyCustom").Profile.Addons.Single(); missing.Folders = ["Missing"];
        await Throws<InvalidDataException>(() =>
        {
            CustomAddonPackages.ExtractVerified(normal.Packages.Single().Value, Path.Combine(p.Follower.Root, "bad-contents"), missing, default);
            return Task.CompletedTask;
        });
    }
    private static async Task Race()
    {
        using var p = new Pair(); var snapshot = await p.Publish("MyCustom");
        var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        p.Runner.AfterDownload = _ =>
        {
            var changed = JsonSerializer.Deserialize<SyncProfile>(JsonSerializer.Serialize(snapshot.Profile))!;
            changed.Revision = Guid.NewGuid(); p.Download.LocalStore.Publish(changed, snapshot.FileHash);
        };
        await Throws<IOException>(() => p.Apply(snapshot));
        Assert(p.Follower.Settings.Addons.Count == 0); Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
    }
    private static async Task Rollback()
    {
        using var p = new Pair(); var snapshot = await p.Publish("MyCustom");
        File.WriteAllText(p.Follower.Store.DirectoryPath, "blocks settings directory");
        var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        await Throws<IOException>(() => p.Apply(snapshot));
        Assert(p.Follower.Settings.Addons.Count == 0); Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
        using var cancelled = new Pair(); var target = await cancelled.Publish("MyCustom");
        using var cancellation = new CancellationTokenSource();
        cancelled.Runner.AfterDownload = _ => cancellation.Cancel();
        await Throws<OperationCanceledException>(() => cancelled.Apply(target, true, cancellation.Token));
        Assert(cancelled.Follower.Settings.Addons.Count == 0 && !Directory.Exists(Path.Combine(cancelled.Follower.AddOns, "MyCustom")));
    }
    private static async Task Extras()
    {
        using var p = new Pair(); await p.Apply(await p.Publish("MyCustom"));
        var snapshot = await p.Publish(); Assert(snapshot.Profile.Addons.Count == 0);
        await p.Apply(snapshot); Assert(Directory.Exists(Path.Combine(p.Follower.AddOns, "MyCustom")));
        await p.Apply(snapshot, true);
        Assert(!Directory.Exists(Path.Combine(p.Follower.AddOns, "MyCustom")) && Directory.Exists(Path.Combine(p.Follower.AddOns, "Unrelated")) && p.Follower.Store.Load().Addons.Count == 0);
    }
}
