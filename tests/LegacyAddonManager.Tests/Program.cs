using System.IO.Compression;
using System.Net;
using System.Text;
using LegacyAddonManager.Core;

if (args.FirstOrDefault() == "rclone-test-wait") { await Task.Delay(Timeout.Infinite); return 0; }
if (args.FirstOrDefault() == "rclone-test-output") { Console.Write(new string('o', 100000)); Console.Error.Write(new string('e', 100000)); return 7; }
if (args.FirstOrDefault() == "rclone-test-args") { Console.Write(string.Join("|", args) + "|rc=" + Environment.GetEnvironmentVariable("RCLONE_RC")); return 0; }

var tests = new (string Name, Func<Task> Run)[]
{
    ("Repository URL validation", () => Sync(TestUrls)),
    ("TOC roots, wrappers, multiple addons, nested libraries", () => Sync(TestDiscovery)),
    ("Unsafe ZIP paths, symlinks, Windows aliases, and duplicates", () => Sync(TestArchives)),
    ("Install/update/remove preserve unmanaged addons", () => Sync(TestLifecycle)),
    ("Unmanaged destination conflicts abort the entire package", () => Sync(TestConflicts)),
    ("Modified and untracked files prevent update/removal", () => Sync(TestModified)),
    ("Cross-repository ownership collision is rejected", () => Sync(TestCrossRepository)),
    ("Settings save failure rolls back install and removal", () => Sync(TestRollback)),
    ("Cancelled install preserves existing version", () => Sync(TestCancellation)),
    ("Corrupt settings are preserved", () => Sync(TestSettings)),
    ("Release ZIPs and source fallback", TestReleasesAsync),
    ("No release uses default branch SHA and pinned archive", TestCommitAsync),
    ("Rate-limit backoff avoids repeated API requests", TestRateLimitAsync),
    ("Authentication errors do not fall back to commits", TestAuthAsync),
    ("Redirects do not forward token to download hosts", TestRedirectAsync),
    ("Unsafe download redirects are rejected", TestUnsafeRedirectAsync)
}.Concat(AdoptionTests.All()).Concat(ProfileTests.All()).Concat(RcloneTests.All()).Concat(CustomSyncTests.All()).ToArray();
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  {test.Name}\n{ex}"); }
}
Console.WriteLine($"\n{tests.Length - failed}/{tests.Length} tests passed.");
if (args.Contains("--live"))
{
    try
    {
        using var fixture = new Fixture();
        using var github = new GitHubService();
        fixture.Addon.RepositoryUrl = "https://github.com/ProfetGit/RefactorGear";
        var version = await github.GetLatestAsync(GitHubRepository.Parse(fixture.Addon.RepositoryUrl), default);
        await fixture.Installer.InstallAsync(fixture.Settings, fixture.Addon, version, version.Downloads.First(), github, null, default);
        Assert(fixture.Addon.InstalledFolders.Count > 0 && fixture.Addon.InstalledVersion == version.Version);
        using var secondComputer = new Fixture();
        secondComputer.Settings.Addons.Clear();
        var profile = new ProfileSyncService(fixture.Store).Export(fixture.Settings).Profile;
        var profileStore = new SyncProfileStore(Path.Combine(fixture.Root, "shared-profile.json"));
        profileStore.Publish(profile, null);
        var sync = new ProfileSyncService(secondComputer.Store);
        await sync.ApplyAsync(secondComputer.Settings, profileStore, profileStore.Read(), false, github, null, default);
        Assert(sync.Preview(secondComputer.Settings, profile).Changes == 0 && secondComputer.Settings.Addons.Single().InstalledVersion == version.Version);
        Console.WriteLine("PASS  Live two-computer profile sync: pinned GitHub version and matching addon contents in isolated WoW folders");
        fixture.Installer.Remove(fixture.Settings, fixture.Addon);
        Console.WriteLine($"PASS  Live ProfetGit/RefactorGear: {version.Kind} {version.Display}, download/install/remove in isolated fake WoW folder");
    }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL  Live GitHub integration: {ex.Message}"); }
}
return failed == 0 ? 0 : 1;

static Task Sync(Action test) { test(); return Task.CompletedTask; }
static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static void TestUrls()
{
    Assert(GitHubRepository.Parse(" https://github.com/ProfetGit/RefactorGear.git/ ").Url == "https://github.com/ProfetGit/RefactorGear");
    foreach (var input in new[] { "http://github.com/a/b", "https://evil.com/a/b", "https://github.com.evil.com/a/b", "https://user@github.com/a/b", "https://github.com/a/b/tree/main", "https://github.com/a/b?q=1", "https://github.com/a/b#x", "https://github.com/a/%2e%2e", "https://github.com:8443/a/b", "git@github.com:a/b.git" })
        Throws<ArgumentException>(() => GitHubRepository.Parse(input));
}
static void Write(string path, string content = "## Interface: 30300\n") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
static void TestDiscovery()
{
    using var f = new Fixture();
    Write(Path.Combine(f.Root, "Repo-main", "First", "First.toc"));
    Write(Path.Combine(f.Root, "Repo-main", "First", "Lib", "Lib.toc"));
    Write(Path.Combine(f.Root, "Repo-main", "Second", "Second_Wrath.toc"));
    var found = AddonArchive.Discover(f.Root);
    Assert(found.Count == 2 && found.Any(x => x.Name == "First") && found.Any(x => x.Name == "Second"));
    using var wrapper = new Fixture();
    Write(Path.Combine(wrapper.Root, "RefactorGear-main", "RefactorGear.toc"));
    Assert(AddonArchive.Discover(wrapper.Root).Single().Name == "RefactorGear");
    using var duplicates = new Fixture();
    Write(Path.Combine(duplicates.Root, "a", "Gear.toc")); Write(Path.Combine(duplicates.Root, "b", "gear.toc"));
    Throws<InvalidDataException>(() => AddonArchive.Discover(duplicates.Root));
}
static void TestArchives()
{
    foreach (var entries in new[] { new[] { "../outside.toc" }, new[] { "C:/escape.toc" }, new[] { "folder/file.toc:stream" }, new[] { "CON.txt" }, new[] { "Foo/a.toc", "foo/A.toc" }, new[] { "folder./a.toc" }, new[] { "/absolute.toc" } })
    {
        using var f = new Fixture();
        var zipPath = Path.Combine(f.Root, "bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write("test"); }
        Throws<InvalidDataException>(() => AddonArchive.Extract(zipPath, Path.Combine(f.Root, "out"), default));
    }
    using var links = new Fixture();
    var linkZip = Path.Combine(links.Root, "link.zip");
    using (var zip = ZipFile.Open(linkZip, ZipArchiveMode.Create)) { var entry = zip.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); }
    Throws<InvalidDataException>(() => AddonArchive.Extract(linkZip, Path.Combine(links.Root, "out"), default));
    using var good = new Fixture();
    var goodZip = Path.Combine(good.Root, "good.zip");
    using (var zip = ZipFile.Open(goodZip, ZipArchiveMode.Create)) { using var w = new StreamWriter(zip.CreateEntry("Repo-main/Foo/Foo.toc").Open()); w.Write("## Interface: 30300"); }
    AddonArchive.Extract(goodZip, Path.Combine(good.Root, "out"), default);
    Assert(AddonArchive.Discover(Path.Combine(good.Root, "out")).Single().Name == "Foo");
}
static void TestLifecycle()
{
    using var f = new Fixture();
    Write(Path.Combine(f.AddOns, "Unmanaged", "Unmanaged.toc"), "keep");
    f.Install("v1", "First", "Second");
    Assert(f.Addon.InstalledVersion == "v1" && f.Store.Load().Addons.Single().InstalledFolders.Count == 2);
    Assert(!Directory.Exists(Path.Combine(f.AddOns, "Repo-main")));
    f.Install("v2", "First", "Third");
    Assert(!Directory.Exists(Path.Combine(f.AddOns, "Second")) && Directory.Exists(Path.Combine(f.AddOns, "Third")));
    f.Installer.Remove(f.Settings, f.Addon);
    Assert(f.Settings.Addons.Count == 0 && !Directory.Exists(Path.Combine(f.AddOns, "First")));
    Assert(File.ReadAllText(Path.Combine(f.AddOns, "Unmanaged", "Unmanaged.toc")) == "keep");
}
static void TestConflicts()
{
    using var f = new Fixture();
    Write(Path.Combine(f.AddOns, "Conflict", "Conflict.toc"), "untouched");
    Throws<IOException>(() => f.Install("v1", "Safe", "Conflict"));
    Assert(!Directory.Exists(Path.Combine(f.AddOns, "Safe")) && f.Addon.InstalledVersion == null);
    Assert(File.ReadAllText(Path.Combine(f.AddOns, "Conflict", "Conflict.toc")) == "untouched");
}
static void TestModified()
{
    using var f = new Fixture(); f.Install("v1", "Foo");
    var toc = Path.Combine(f.AddOns, "Foo", "Foo.toc");
    var original = File.ReadAllText(toc);
    File.WriteAllText(toc, "local modification");
    Throws<IOException>(() => f.Install("v2", "Foo"));
    Throws<IOException>(() => f.Installer.Remove(f.Settings, f.Addon));
    Assert(f.Addon.InstalledVersion == "v1" && File.ReadAllText(toc) == "local modification");
    File.WriteAllText(toc, original);
    Write(Path.Combine(f.AddOns, "Foo", "untracked.txt"));
    Throws<IOException>(() => f.Installer.Remove(f.Settings, f.Addon));
    File.Delete(Path.Combine(f.AddOns, "Foo", "untracked.txt"));
    Directory.CreateDirectory(Path.Combine(f.AddOns, "Foo", "UntrackedEmptyFolder"));
    Throws<IOException>(() => f.Installer.Remove(f.Settings, f.Addon));
}
static void TestCrossRepository()
{
    using var f = new Fixture(); f.Install("v1", "Foo");
    var second = new ManagedAddon { Name = "Other", RepositoryUrl = "https://github.com/owner/Other" };
    f.Settings.Addons.Add(second);
    Throws<IOException>(() => f.Installer.InstallExtracted(f.Settings, second, new("v1", "release", []), new("source", "", true), f.Package("Foo")));
    Assert(f.Addon.InstalledVersion == "v1");
}
static void TestRollback()
{
    using var f = new Fixture(); f.Install("v1", "First", "Second");
    var oldToc = File.ReadAllText(Path.Combine(f.AddOns, "First", "First.toc"));
    File.Delete(f.Store.FilePath); Directory.CreateDirectory(f.Store.FilePath); // Force the atomic settings replacement to fail.
    Throws<UnauthorizedAccessException>(() => f.Install("v2", "First", "Third"));
    Assert(f.Addon.InstalledVersion == "v1" && Directory.Exists(Path.Combine(f.AddOns, "Second")) && !Directory.Exists(Path.Combine(f.AddOns, "Third")));
    Assert(File.ReadAllText(Path.Combine(f.AddOns, "First", "First.toc")) == oldToc);
    Throws<UnauthorizedAccessException>(() => f.Installer.Remove(f.Settings, f.Addon));
    Assert(f.Settings.Addons.Contains(f.Addon) && Directory.Exists(Path.Combine(f.AddOns, "First")));
    Assert(!Directory.EnumerateDirectories(f.AddOns, ".lam-transaction-*").Any());
}
static void TestCancellation()
{
    using var f = new Fixture(); f.Install("v1", "Foo");
    using var ct = new CancellationTokenSource(); ct.Cancel();
    Throws<OperationCanceledException>(() => f.Installer.InstallExtracted(f.Settings, f.Addon, new("v2", "release", []), new("source", "", true), f.Package("Foo"), ct: ct.Token));
    Assert(f.Addon.InstalledVersion == "v1" && Directory.Exists(Path.Combine(f.AddOns, "Foo")));
}
static void TestSettings()
{
    using var f = new Fixture(); Directory.CreateDirectory(f.Store.DirectoryPath);
    File.WriteAllText(f.Store.FilePath, "{invalid json");
    Throws<InvalidDataException>(() => f.Store.Load());
    Assert(File.ReadAllText(f.Store.FilePath) == "{invalid json");
    File.WriteAllText(f.Store.FilePath, "{\"SchemaVersion\":9}");
    Throws<InvalidDataException>(() => f.Store.Load());
}
static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static async Task TestReleasesAsync()
{
    using var handler = new StubHandler(request =>
    {
        Assert(request.Headers.UserAgent.ToString().Contains("LegacyAddonManager"));
        Assert(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest"));
        return Json("{\"tag_name\":\"v1.2\",\"assets\":[{\"name\":\"Wrath.zip\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v1.2/Wrath.zip\"},{\"name\":\"readme.txt\"}]}" );
    });
    using var github = new GitHubService(handler);
    var result = await github.GetLatestAsync(new("o", "r"), default);
    Assert(result.Version == "v1.2" && result.Kind == "release" && result.Downloads.Count == 2 && result.Downloads.Last().IsSourceArchive);
}
static async Task TestCommitAsync()
{
    const string sha = "0123456789abcdef0123456789abcdef01234567";
    using var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
    {
        "/repos/o/r/releases/latest" => Json("{}", HttpStatusCode.NotFound),
        "/repos/o/r" => Json("{\"default_branch\":\"wrath/main\"}"),
        _ => Json("{\"sha\":\"" + sha + "\"}")
    });
    using var github = new GitHubService(handler);
    var result = await github.GetLatestAsync(new("o", "r"), default);
    Assert(result.Kind == "commit" && result.Version == sha && result.Downloads.Single().Url.EndsWith(sha));
    Assert(handler.Calls == 3 && handler.Paths.Last().Contains("wrath%2Fmain"));
}
static async Task TestRateLimitAsync()
{
    using var handler = new StubHandler(_ =>
    {
        var response = Json("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
        return response;
    });
    using var github = new GitHubService(handler);
    await ThrowsAsync<GitHubRateLimitException>(() => github.GetLatestAsync(new("o", "r"), default));
    await ThrowsAsync<GitHubRateLimitException>(() => github.GetLatestAsync(new("o", "r"), default));
    Assert(handler.Calls == 1);
    github.ResetRateLimit();
    await ThrowsAsync<GitHubRateLimitException>(() => github.GetLatestAsync(new("o", "r"), default));
    Assert(handler.Calls == 2);
}
static async Task TestAuthAsync()
{
    using var handler = new StubHandler(_ => Json("{}", HttpStatusCode.Unauthorized));
    using var github = new GitHubService(handler) { Token = "test-token" };
    await ThrowsAsync<HttpRequestException>(() => github.GetLatestAsync(new("o", "r"), default));
    Assert(handler.Calls == 1);
}
static async Task TestRedirectAsync()
{
    using var f = new Fixture();
    using var handler = new StubHandler(request =>
    {
        if (request.RequestUri!.Host == "api.github.com")
        {
            Assert(request.Headers.Authorization?.Parameter == "test-token");
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new("https://codeload.github.com/o/r/zip/sha"); return response;
        }
        Assert(request.Headers.Authorization == null);
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
    });
    using var github = new GitHubService(handler) { Token = "test-token" };
    await github.DownloadAsync("https://api.github.com/repos/o/r/zipball/sha", Path.Combine(f.Root, "download.zip"), null, default);
    Assert(handler.Calls == 2);
}
static async Task TestUnsafeRedirectAsync()
{
    using var f = new Fixture();
    using var handler = new StubHandler(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new("https://evil.example/file.zip"); return response;
    });
    using var github = new GitHubService(handler);
    await ThrowsAsync<InvalidDataException>(() => github.DownloadAsync("https://github.com/o/r/file.zip", Path.Combine(f.Root, "download.zip"), null, default));
    Assert(handler.Calls == 1);
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<string> Paths { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; Paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(callback(request)); }
}
sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), ".lam-tests-" + Guid.NewGuid().ToString("N"));
    public string AddOns => Path.Combine(Settings.WoWPath, "Interface", "AddOns");
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public ManagedAddon Addon { get; } = new() { Name = "Example", RepositoryUrl = "https://github.com/owner/Example" };
    public AddonInstaller Installer { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Settings = new() { WoWPath = Path.Combine(Root, "WoW"), Addons = [Addon] };
        Directory.CreateDirectory(Settings.WoWPath);
        File.WriteAllText(Path.Combine(Settings.WoWPath, "Wow.exe"), "fixture");
        Store = new(Path.Combine(Root, "settings")); Installer = new(Store);
    }
    public IReadOnlyList<AddonFolder> Package(params string[] names)
    {
        var source = Path.Combine(Root, "package-" + Guid.NewGuid().ToString("N"));
        foreach (var name in names)
        {
            var folder = Path.Combine(source, "Repo-main", name); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".toc"), "## Interface: 30300\n" + Guid.NewGuid());
        }
        return AddonArchive.Discover(source);
    }
    public void Install(string version, params string[] names) => Installer.InstallExtracted(Settings, Addon, new(version, "release", []), new("source", "", true), Package(names));
    public void Dispose() => SafeFiles.DeleteOwnedWorkDirectory(Root, Path.GetTempPath());
}
