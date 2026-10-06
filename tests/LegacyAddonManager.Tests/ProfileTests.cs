using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LegacyAddonManager.Core;

internal static class ProfileTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("Portable profile roundtrip excludes credentials, paths and unestablished sources", Export);
        yield return ("Two-computer sync pins an older multi-addon release and preserves local data", TwoComputers);
        yield return ("Profile source choice does not switch to the only release ZIP asset", SourceChoice);
        yield return ("Commit profile downloads recorded SHA without default branch lookup", Commit);
        yield return ("Changed GitHub package content is rejected before replacing local files", ContentMismatch);
        yield return ("Missing recorded release asset does not fall back to source or latest", MissingAsset);
        yield return ("Profile changes and foreign publishers cannot overwrite a shared profile", Conflicts);
        yield return ("Profile changed during download is rejected before installed folders change", MidDownloadChange);
        yield return ("Unmanaged collisions and locally edited managed addons block sync", LocalProtection);
        yield return ("Extra managed sources require explicit removal; unmanaged folders survive", Extras);
        yield return ("Failed or cancelled sync preserves extras and new-record tracking", FailedSync);
        yield return ("Corrupt, unsafe and unsupported profiles are preserved and rejected", InvalidProfiles);
        yield return ("Sync settings and publisher identity survive restart without changing old ownership", LocalSettings);
        yield return ("Empty profile represents removal of the last source with explicit follower removal", EmptyProfile);
        yield return ("Equivalent repository URLs cannot turn a desired source into an extra for removal", CanonicalUrls);
    }
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Same(Dictionary<string, string> before, Dictionary<string, string> after) =>
        Assert(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value), "Files unexpectedly changed.");

    private sealed class Pair : IDisposable
    {
        public Fixture Publisher { get; } = new();
        public Fixture Follower { get; } = new();
        public IReadOnlyList<AddonFolder> Package { get; }
        public byte[] Zip { get; }
        public SyncProfileStore Store { get; }
        public ProfileSyncService Sync { get; }
        public SyncProfile Profile { get; }
        public Pair(string kind = "release", bool asset = true)
        {
            Follower.Settings.Addons.Clear();
            Package = Publisher.Package("First", "Second");
            var version = kind == "release" ? "v1.0" : "0123456789abcdef0123456789abcdef01234567";
            Publisher.Installer.InstallExtracted(Publisher.Settings, Publisher.Addon, new(version, kind, []), new("Wrath.zip", "", !asset), Package);
            Profile = new ProfileSyncService(Publisher.Store).Export(Publisher.Settings).Profile;
            Store = new(Path.Combine(Publisher.Root, "shared-profile.json"));
            Store.Publish(Profile, null);
            Sync = new(Follower.Store);
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var folder in Package)
                    foreach (var file in Directory.EnumerateFiles(folder.SourcePath, "*", SearchOption.AllDirectories))
                    {
                        using var entry = zip.CreateEntry("Repository-old/" + folder.Name + "/" + Path.GetRelativePath(folder.SourcePath, file).Replace('\\', '/')).Open();
                        using var input = File.OpenRead(file); input.CopyTo(entry);
                    }
            Zip = output.ToArray();
        }
        public GitHubService GitHub(List<string>? paths = null, byte[]? zip = null, bool missingAsset = false, bool fail = false) => new(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths?.Add(path);
            Assert(!path.EndsWith("/latest") && !path.Contains("/commits/"), "Sync must never look up latest or a branch.");
            if (fail) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("network failure") };
            if (path.Contains("/releases/tags/"))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = Profile.Addons[0].Version,
                    assets = missingAsset ? Array.Empty<object>() : new object[] { new { name = "Wrath.zip", browser_download_url = "https://github.com/owner/Example/releases/download/v1.0/Wrath.zip" } }
                })) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(zip ?? Zip) };
        }));
        public Task<int> Apply(bool remove = false, GitHubService? github = null, CancellationToken ct = default) =>
            Sync.ApplyAsync(Follower.Settings, Store, Store.Read(), remove, github ?? GitHub(), null, ct);
        public void Dispose() { Publisher.Dispose(); Follower.Dispose(); }
    }

    private static Task Export()
    {
        using var p = new Pair();
        p.Publisher.Settings.ProtectedGitHubToken = "local-secret";
        p.Publisher.Settings.Addons.Add(new() { Name = "Pending", RepositoryUrl = "https://github.com/owner/Pending" });
        var export = new ProfileSyncService(p.Publisher.Store).Export(p.Publisher.Settings);
        Assert(export.Excluded.Count == 1 && export.Profile.Addons.Single().Folders.Count == 2);
        var text = File.ReadAllText(p.Store.FilePath);
        Assert(!text.Contains("local-secret") && !text.Contains("WoWPath") && !text.Contains("InstallationId") && !text.Contains(p.Publisher.Settings.WoWPath));
        Assert(p.Store.Read().Profile.Addons.Single().Version == "v1.0");
        return Task.CompletedTask;
    }
    private static async Task TwoComputers()
    {
        using var p = new Pair();
        var paths = new List<string>();
        using var github = p.GitHub(paths);
        var localPath = p.Follower.Settings.WoWPath;
        p.Follower.Settings.ProtectedGitHubToken = "follower-only";
        var wtf = Path.Combine(localPath, "WTF", "Account", "SavedVariables", "First.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(wtf)!); File.WriteAllText(wtf, "my layout");
        var unmanaged = Path.Combine(p.Follower.AddOns, "Unmanaged"); Directory.CreateDirectory(unmanaged); File.WriteAllText(Path.Combine(unmanaged, "Unmanaged.toc"), "keep");
        var plan = p.Sync.Preview(p.Follower.Settings, p.Profile);
        Assert(plan.Changes == 1 && plan.UnmanagedCount == 1 && plan.Items.Single().Action == "Install");
        Assert(await p.Apply(github: github) == 1);
        var installed = p.Follower.Settings.Addons.Single();
        Assert(installed.InstalledVersion == "v1.0" && installed.PreferredAssetName == "Wrath.zip" && installed.InstalledFolders.Count == 2);
        Assert(installed.Id != p.Publisher.Addon.Id && installed.InstallationId != p.Publisher.Addon.InstallationId);
        Assert(p.Follower.Settings.WoWPath == localPath && p.Follower.Settings.ProtectedGitHubToken == "follower-only" && File.ReadAllText(wtf) == "my layout");
        Assert(File.ReadAllText(Path.Combine(unmanaged, "Unmanaged.toc")) == "keep");
        Assert(paths.Count == 2 && paths.First().EndsWith("/releases/tags/v1.0"));
        Assert(p.Sync.Preview(p.Follower.Settings, p.Profile).Changes == 0 && await p.Apply(github: github) == 0 && paths.Count == 2);
        // Explicitly apply an older version, even when this computer records a newer one.
        installed.InstalledVersion = "v9.0";
        Assert(p.Sync.Preview(p.Follower.Settings, p.Profile).Items.Single().Detail.Contains("v9.0 → v1.0"));
        Assert(await p.Apply(github: github) == 1 && installed.InstalledVersion == "v1.0");
        Assert(!Directory.Exists(Path.Combine(p.Follower.AddOns, "Repository-old")));
    }
    private static async Task SourceChoice()
    {
        using var p = new Pair(asset: false);
        var paths = new List<string>(); using var github = p.GitHub(paths);
        await p.Apply(github: github);
        Assert(paths.Last().EndsWith("/zipball/v1.0") && p.Follower.Settings.Addons.Single().PreferredAssetName == null);
    }
    private static async Task Commit()
    {
        using var p = new Pair("commit", false);
        var paths = new List<string>(); using var github = p.GitHub(paths);
        await p.Apply(github: github);
        Assert(paths.Count == 1 && paths[0].EndsWith(p.Profile.Addons.Single().Version));
        Assert(p.Follower.Settings.Addons.Single().InstalledVersionKind == "commit");
    }
    private static async Task ContentMismatch()
    {
        using var p = new Pair(); using var github = p.GitHub(); await p.Apply(github: github);
        var installed = p.Follower.Settings.Addons.Single(); installed.InstalledVersion = "v0.5"; p.Follower.Store.Save(p.Follower.Settings);
        var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var name in new[] { "First", "Second" }) { using var writer = new StreamWriter(zip.CreateEntry($"Repo/{name}/{name}.toc").Open()); writer.Write("changed content"); }
        using var changed = p.GitHub(zip: bytes.ToArray());
        await ThrowsAsync<InvalidDataException>(() => p.Apply(github: changed));
        Same(before, SafeFiles.HashFiles(p.Follower.AddOns, "")); Assert(installed.InstalledVersion == "v0.5");
        Assert(!Directory.EnumerateDirectories(p.Follower.AddOns, ".lam-transaction-*").Any());
    }
    private static async Task MissingAsset()
    {
        using var p = new Pair(); var paths = new List<string>(); using var github = p.GitHub(paths, missingAsset: true);
        await ThrowsAsync<IOException>(() => p.Apply(github: github));
        Assert(paths.Count == 1 && p.Follower.Settings.Addons.Count == 0 && !Directory.Exists(p.Follower.AddOns));
    }
    private static async Task Conflicts()
    {
        using var p = new Pair(); var before = p.Store.Read();
        var another = new ProfileSyncService(p.Publisher.Store).Export(p.Publisher.Settings).Profile;
        p.Store.Publish(another, before.FileHash);
        Throws<IOException>(() => p.Store.Publish(p.Profile, before.FileHash));
        using var github = p.GitHub();
        await ThrowsAsync<IOException>(() => p.Sync.ApplyAsync(p.Follower.Settings, p.Store, before, false, github, null, default));
        var bytes = File.ReadAllBytes(p.Store.FilePath);
        another.PublisherId = Guid.NewGuid();
        Throws<IOException>(() => p.Store.Publish(another, p.Store.Read().FileHash));
        Assert(bytes.SequenceEqual(File.ReadAllBytes(p.Store.FilePath)) && p.Follower.Settings.Addons.Count == 0);
    }
    private static async Task LocalProtection()
    {
        using var p = new Pair();
        var collision = Path.Combine(p.Follower.AddOns, "First"); Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "First.toc"), "unmanaged");
        Throws<IOException>(() => p.Sync.Preview(p.Follower.Settings, p.Profile));
        Assert(File.ReadAllText(Path.Combine(collision, "First.toc")) == "unmanaged");
        // Explicit association allows a first manager replacement after the sync preview.
        new AddonManagement(p.Follower.Store).AssignSource(p.Follower.Settings, "First", p.Profile.Addons.Single().RepositoryUrl);
        Assert(p.Sync.Preview(p.Follower.Settings, p.Profile).Items.Single().Detail.Contains("adopted"));
        using var github = p.GitHub(); await p.Apply(github: github);
        File.WriteAllText(Path.Combine(collision, "local.txt"), "preserve");
        var before = SafeFiles.HashFiles(p.Follower.AddOns, "");
        Throws<IOException>(() => p.Sync.Preview(p.Follower.Settings, p.Profile));
        Same(before, SafeFiles.HashFiles(p.Follower.AddOns, ""));
    }
    private static async Task MidDownloadChange()
    {
        using var p = new Pair();
        var snapshot = p.Store.Read();
        using var github = new GitHubService(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/tags/"))
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v1.0\",\"assets\":[{\"name\":\"Wrath.zip\",\"browser_download_url\":\"https://github.com/owner/Example/download.zip\"}]}") };
            var changed = new ProfileSyncService(p.Publisher.Store).Export(p.Publisher.Settings).Profile;
            p.Store.Publish(changed, snapshot.FileHash);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(p.Zip) };
        }));
        await ThrowsAsync<IOException>(() => p.Sync.ApplyAsync(p.Follower.Settings, p.Store, snapshot, false, github, null, default));
        Assert(p.Follower.Settings.Addons.Count == 0 && !Directory.Exists(Path.Combine(p.Follower.AddOns, "First")) && !Directory.Exists(Path.Combine(p.Follower.AddOns, "Second")));
    }
    private static async Task Extras()
    {
        using var p = new Pair();
        var extra = new ManagedAddon { Name = "Extra", RepositoryUrl = "https://github.com/owner/Extra" };
        p.Follower.Settings.Addons.Add(extra);
        p.Follower.Installer.InstallExtracted(p.Follower.Settings, extra, new("v1", "release", []), new("source", "", true), p.Follower.Package("Extra"));
        var unmanaged = Path.Combine(p.Follower.AddOns, "Unmanaged"); Directory.CreateDirectory(unmanaged); File.WriteAllText(Path.Combine(unmanaged, "Unmanaged.toc"), "untouched");
        using var github = p.GitHub();
        await p.Apply(github: github);
        Assert(Directory.Exists(Path.Combine(p.Follower.AddOns, "Extra")) && p.Sync.Preview(p.Follower.Settings, p.Profile).Extras.Count == 1);
        await p.Apply(remove: true, github: github);
        Assert(!Directory.Exists(Path.Combine(p.Follower.AddOns, "Extra")) && p.Follower.Settings.Addons.Count == 1);
        Assert(File.ReadAllText(Path.Combine(unmanaged, "Unmanaged.toc")) == "untouched");
    }
    private static async Task FailedSync()
    {
        using var p = new Pair();
        var extra = new ManagedAddon { Name = "Extra", RepositoryUrl = "https://github.com/owner/Extra" };
        p.Follower.Settings.Addons.Add(extra);
        p.Follower.Installer.InstallExtracted(p.Follower.Settings, extra, new("v1", "release", []), new("source", "", true), p.Follower.Package("Extra"));
        using var failed = p.GitHub(fail: true);
        await ThrowsAsync<HttpRequestException>(() => p.Apply(true, failed));
        Assert(p.Follower.Settings.Addons.Count == 1 && Directory.Exists(Path.Combine(p.Follower.AddOns, "Extra")));
        using var ct = new CancellationTokenSource(); ct.Cancel(); using var github = p.GitHub();
        await ThrowsAsync<OperationCanceledException>(() => p.Apply(true, github, ct.Token));
        // Settings-save failure rolls back the install and removes the transient new tracking record.
        File.Delete(p.Follower.Store.FilePath); Directory.CreateDirectory(p.Follower.Store.FilePath);
        await ThrowsAsync<UnauthorizedAccessException>(() => p.Apply(true, github));
        Assert(p.Follower.Settings.Addons.Count == 1 && !Directory.Exists(Path.Combine(p.Follower.AddOns, "First")) && Directory.Exists(Path.Combine(p.Follower.AddOns, "Extra")));
    }
    private static Task InvalidProfiles()
    {
        using var p = new Pair(); var original = File.ReadAllText(p.Store.FilePath);
        foreach (var text in new[] { "{broken", original.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 9"),
            original.Replace("\"First\"", "\"../First\""), original.Replace("\"PublisherId\"", "\"WoWPath\""),
            original.Replace("\"Folders\": [", "\"Folders\": null, \"Unknown\": [") })
        { File.WriteAllText(p.Store.FilePath, text); Throws<InvalidDataException>(() => p.Store.Read()); Assert(File.ReadAllText(p.Store.FilePath) == text); }
        File.WriteAllText(p.Store.FilePath, original);
        var addon = p.Profile.Addons.Single(); p.Profile.Addons.Add(addon);
        Throws<InvalidDataException>(() => SyncProfileStore.Validate(p.Profile));
        return Task.CompletedTask;
    }
    private static Task LocalSettings()
    {
        using var p = new Pair();
        var installationId = p.Publisher.Addon.InstallationId;
        p.Publisher.Settings.RcloneConfigPath = Path.Combine(p.Publisher.Store.DirectoryPath, "rclone.conf");
        p.Publisher.Settings.RcloneRemoteProfile = "cloud:WoW/addons.json";
        p.Publisher.Settings.ProtectedRclonePassword = "local-protected-password";
        p.Publisher.Settings.SyncIsPublisher = true; p.Publisher.Store.Save(p.Publisher.Settings);
        var loaded = p.Publisher.Store.Load();
        Assert(loaded.SyncPublisherId == p.Publisher.Settings.SyncPublisherId && loaded.SyncIsPublisher);
        Assert(loaded.RcloneConfigPath == p.Publisher.Settings.RcloneConfigPath && loaded.RcloneRemoteProfile == "cloud:WoW/addons.json" &&
            loaded.ProtectedRclonePassword == "local-protected-password", "rclone connection and credentials must survive restart.");
        Assert(loaded.Addons.Single().InstallationId == installationId);
        var old = File.ReadAllText(p.Publisher.Store.FilePath);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(old)!.AsObject();
        legacy["SyncProfilePath"] = p.Store.FilePath; legacy["SyncTransport"] = "Rclone";
        File.WriteAllText(p.Publisher.Store.FilePath, legacy.ToJsonString());
        loaded = p.Publisher.Store.Load();
        Assert(loaded.RcloneRemoteProfile == "cloud:WoW/addons.json" && loaded.Addons.Single().InstallationId == installationId &&
            loaded.SyncPublisherId == p.Publisher.Settings.SyncPublisherId, "Old rclone settings must retain connections and ownership.");
        p.Publisher.Store.Save(loaded);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p.Publisher.Store.FilePath))!.AsObject();
        Assert(!saved.ContainsKey("SyncProfilePath") && !saved.ContainsKey("SyncTransport"), "Saving must drop obsolete shared-file fields.");
        legacy["SyncTransport"] = "File";
        legacy.Remove("RcloneConfigPath"); legacy.Remove("RcloneRemoteProfile"); legacy.Remove("ProtectedRclonePassword");
        File.WriteAllText(p.Publisher.Store.FilePath, legacy.ToJsonString());
        loaded = p.Publisher.Store.Load();
        Assert(loaded.RcloneConfigPath == "" && loaded.RcloneRemoteProfile == "" && loaded.Addons.Single().InstallationId == installationId,
            "Old shared-file settings must load with no rclone connection and preserve addon ownership.");
        return Task.CompletedTask;
    }
    private static async Task EmptyProfile()
    {
        using var p = new Pair(); using var github = p.GitHub(); await p.Apply(github: github);
        p.Publisher.Settings.Addons.Clear();
        var empty = new ProfileSyncService(p.Publisher.Store).Export(p.Publisher.Settings).Profile;
        Assert(empty.Addons.Count == 0);
        p.Store.Publish(empty, p.Store.Read().FileHash);
        await p.Apply(github: github);
        Assert(p.Follower.Settings.Addons.Count == 1, "An empty profile must not automatically uninstall everything.");
        await p.Apply(true, github);
        Assert(p.Follower.Settings.Addons.Count == 0 && !Directory.Exists(Path.Combine(p.Follower.AddOns, "First")));
    }
    private static async Task CanonicalUrls()
    {
        using var p = new Pair(); using var github = p.GitHub(); await p.Apply(github: github);
        var text = File.ReadAllText(p.Store.FilePath).Replace("https://github.com/owner/Example", "https://github.com/owner/Example.git/");
        File.WriteAllText(p.Store.FilePath, text);
        var snapshot = p.Store.Read();
        var plan = p.Sync.Preview(p.Follower.Settings, snapshot.Profile);
        Assert(plan.Extras.Count == 0 && plan.Changes == 0);
        Assert(await p.Apply(true, github) == 0 && p.Follower.Settings.Addons.Count == 1 && Directory.Exists(Path.Combine(p.Follower.AddOns, "First")));
    }
}
