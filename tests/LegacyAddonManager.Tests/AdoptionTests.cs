using System.Text.Json;
using LegacyAddonManager.Core;

internal static class AdoptionTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Test("TOC metadata, color stripping, dependency aliases", Metadata);
        yield return Test("Live inventory discovers unmanaged addons without JSON entries", Inventory);
        yield return Test("Assigning a source preserves all files and survives restart", Adopt);
        yield return Test("Release comparison distinguishes current, different, and unknown", Versions);
        yield return Test("Same-source manual folders share one repository record", MultiFolder);
        yield return Test("First adopted update stages every associated folder safely", FirstUpdate);
        yield return Test("Adopted edits and untracked files block first update", ChangedAdoption);
        yield return Test("First adopted update rolls back on settings failure", AdoptedRollback);
        yield return Test("Stop Managing preserves manual and manager-installed files", StopManaging);
        yield return Test("Reassigning one source leaves sibling associations intact", Reassign);
        yield return Test("WoW path changes preserve independent same-name installations", MultipleWoWPaths);
        yield return Test("Version 1 migration preserves managed ownership and backs up JSON", Migration);
        yield return Test("Missing managed folders retain their source association", Missing);
        yield return Test("Adoption persistence failure and cancellation preserve files", AdoptionFailure);
    }
    private static (string, Func<Task>) Test(string name, Action test) => (name, () => { test(); return Task.CompletedTask; });
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void EqualTree(Dictionary<string, string> before, Dictionary<string, string> after) =>
        Assert(before.Count == after.Count && before.All(f => after.TryGetValue(f.Key, out var value) && value == f.Value), "Addon file tree changed unexpectedly.");
    private static void Manual(Fixture fixture, string name, string version = "1.2.3", string? title = null)
    {
        var folder = Path.Combine(fixture.AddOns, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".toc"), $"## Interface: 30300\n## Title: {title ?? name}\n## Version: {version}\n## Author: Test Author\n## Notes: A manually installed addon\n");
        File.WriteAllText(Path.Combine(folder, "main.lua"), "-- local addon code");
    }
    private static void Metadata()
    {
        var m = TocReader.Parse("\uFEFF## Title: |cffffcc00My Addon|r\r\n## version: v1.2.3\n## Notes: Useful notes: with colon\n## Author: Someone\n## Interface: 30300\n## SavedVariables: FooDB, BarDB\n## SavedVariablesPerCharacter: CharacterDB\n## RequiredDeps: Core, Lib\n## OptionalDeps: Skin\n# ignored\nmain.lua");
        Assert(m.Title == "My Addon" && m.Version == "v1.2.3" && m.Notes == "Useful notes: with colon" && m.Author == "Someone");
        Assert(m.Interface == "30300" && m.SavedVariables == "FooDB, BarDB" && m.SavedVariablesPerCharacter == "CharacterDB" && m.Dependencies == "Core, Lib" && m.OptionalDependencies == "Skin");
        using var f = new Fixture(); Manual(f, "Fallback");
        File.WriteAllText(Path.Combine(f.AddOns, "Fallback", "Fallback.toc"), "## Interface: 30300");
        Assert(new AddonInventory().Read(f.Settings).Entries.Single(e => e.FolderName == "Fallback").DisplayName == "Fallback");
    }
    private static void Inventory()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear();
        Manual(f, "Foo", title: "Friendly Foo"); Manual(f, "Bar");
        Directory.CreateDirectory(Path.Combine(f.AddOns, "NotAnAddon"));
        Directory.CreateDirectory(Path.Combine(f.AddOns, "Foo", "Library"));
        File.WriteAllText(Path.Combine(f.AddOns, "Foo", "Library", "Library.toc"), "## Title: Nested library");
        File.WriteAllText(Path.Combine(f.AddOns, "Foo", "Foo_Wrath.toc"), "## Title: Alternate TOC\n## Version: wrong");
        var inventory = new AddonInventory();
        var first = inventory.Read(f.Settings);
        Assert(first.Entries.Count == 2 && first.Entries.All(e => e.IsInstalled && e.Management == null));
        var foo = first.Entries.Single(e => e.FolderName == "Foo");
        Assert(foo.DisplayName == "Friendly Foo" && foo.Local!.Metadata.Version == "1.2.3" && foo.Local.TocFile == "Foo.toc");
        Assert(!File.Exists(f.Store.FilePath) && f.Settings.Addons.Count == 0);
        Manual(f, "Third");
        Assert(inventory.Read(f.Settings).Entries.Count == 3);
        Directory.Move(Path.Combine(f.AddOns, "Bar"), Path.Combine(f.Root, "Bar-backup"));
        Assert(inventory.Read(f.Settings).Entries.Count == 2);
    }
    private static void Adopt()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo", title: "Friendly Foo");
        var before = SafeFiles.HashFiles(f.AddOns, "");
        var record = new AddonManagement(f.Store).AssignSource(f.Settings, "Foo", " https://github.com/owner/Example.git/ ");
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        Assert(record.RepositoryUrl == "https://github.com/owner/Example" && record.InstalledVersion == null && record.InstallationId == null);
        Assert(record.Origin == "Manual" && record.WasDiscoveredManually && record.InstalledTocVersions["Foo"] == "1.2.3");
        Assert(!File.Exists(Path.Combine(f.AddOns, "Foo", AddonInstaller.MarkerFile)));
        var loaded = f.Store.Load();
        var entry = new AddonInventory().Read(loaded).Entries.Single();
        Assert(entry.IsInstalled && entry.Management?.RepositoryUrl == record.RepositoryUrl && entry.DisplayName == "Friendly Foo");
        AddonInstaller.ValidateOwnership(f.AddOns, entry.Management!, "Foo");
    }
    private static void Versions()
    {
        var manual = new ManagedAddon { AdoptedFolders = [new() { Name = "Foo" }] };
        var release = new RepositoryVersion("v1.2.3", "release", []);
        foreach (var version in new[] { "1.2.3", "v1.2.3", "01.002.003", "1.2.3+local" })
            Assert(AddonVersionComparer.Compare(manual, "Foo", version, release) == VersionComparison.Current);
        Assert(AddonVersionComparer.Compare(manual, "Foo", "1.2.2", release) == VersionComparison.Different);
        Assert(AddonVersionComparer.Compare(manual, "Foo", "2.0.0", release) == VersionComparison.Different);
        foreach (var version in new[] { "", "@project-version@", "$Revision$", "development", "r123", "custom build" })
            Assert(AddonVersionComparer.Compare(manual, "Foo", version, release) == VersionComparison.Unknown);
        Assert(AddonVersionComparer.Compare(manual, "Foo", "1.2.3", new("0123456789abcdef", "commit", [])) == VersionComparison.Unknown);
        Assert(AddonVersionComparer.Compare(manual, "Foo", "release-one", new("release-one", "release", [])) == VersionComparison.Current);
        var installed = new ManagedAddon { InstalledVersion = "v1.2.3", InstalledVersionKind = "release" };
        Assert(AddonVersionComparer.Compare(installed, "Foo", "custom", release) == VersionComparison.Current);
        Assert(AddonVersionComparer.Compare(installed, "Foo", "1.2.3", new("v1.2.4", "release", [])) == VersionComparison.Different);
    }
    private static void MultiFolder()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear();
        var service = new AddonManagement(f.Store);
        foreach (var name in new[] { "DBM-Core", "DBM-GUI", "DBM-Icecrown" }) Manual(f, name);
        var first = service.AssignSource(f.Settings, "DBM-Core", "https://github.com/owner/DBM");
        service.AssignSource(f.Settings, "DBM-GUI", "https://github.com/OWNER/dbm.git/");
        service.AssignSource(f.Settings, "DBM-Icecrown", "https://github.com/owner/DBM");
        Assert(f.Settings.Addons.Count == 1 && first.InstalledFolders.Count == 3 && first.AdoptedFolders.Count == 3);
        var rows = new AddonInventory().Read(f.Store.Load()).Entries;
        Assert(rows.Count == 3 && rows.All(r => r.Management?.Id == first.Id));
        var toc = Path.Combine(f.AddOns, "DBM-Core", "DBM-Core.toc");
        File.AppendAllText(toc, "\n## Notes: Local edit");
        service.AssignSource(f.Settings, "DBM-Core", first.RepositoryUrl); // Same source must never silently reset the adoption baseline.
        Throws<IOException>(() => AddonInstaller.ValidateOwnership(f.AddOns, first, "DBM-Core"));
    }
    private static void FirstUpdate()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear();
        Manual(f, "Foo"); Manual(f, "Bar"); Manual(f, "Unrelated");
        var other = SafeFiles.HashFiles(Path.Combine(f.AddOns, "Unrelated"), "");
        var service = new AddonManagement(f.Store);
        var record = service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        service.AssignSource(f.Settings, "Bar", record.RepositoryUrl);
        var package = f.Package("Foo", "Baz");
        File.WriteAllText(Path.Combine(package.First(p => p.Name == "Foo").SourcePath, "Foo.toc"), "## Title: New Foo\n## Version: 2.0.0\n## Interface: 30300");
        f.Installer.InstallExtracted(f.Settings, record, new("v2.0.0", "release", []), new("source", "", true), package);
        Assert(record.InstalledVersion == "v2.0.0" && record.AdoptedFolders.Count == 0 && record.Origin == "Manager" && record.WasDiscoveredManually);
        Assert(record.InstalledTocVersions["Foo"] == "2.0.0" && record.FolderDisplayNames["Foo"] == "New Foo");
        Assert(Directory.Exists(Path.Combine(f.AddOns, "Baz")) && !Directory.Exists(Path.Combine(f.AddOns, "Bar")));
        EqualTree(other, SafeFiles.HashFiles(Path.Combine(f.AddOns, "Unrelated"), ""));
        AddonInstaller.ValidateOwnership(f.AddOns, record, "Foo");
        Assert(f.Store.Load().Addons.Single().InstalledVersion == "v2.0.0");
    }
    private static void ChangedAdoption()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo");
        var record = new AddonManagement(f.Store).AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        File.WriteAllText(Path.Combine(f.AddOns, "Foo", "extra.txt"), "preserve");
        Throws<IOException>(() => f.Installer.InstallExtracted(f.Settings, record, new("v2", "release", []), new("source", "", true), f.Package("Foo")));
        Throws<IOException>(() => f.Installer.Remove(f.Settings, record));
        Assert(File.ReadAllText(Path.Combine(f.AddOns, "Foo", "extra.txt")) == "preserve" && record.InstalledVersion == null);
    }
    private static void AdoptedRollback()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo");
        var record = new AddonManagement(f.Store).AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        var before = SafeFiles.HashFiles(f.AddOns, "");
        File.Delete(f.Store.FilePath); Directory.CreateDirectory(f.Store.FilePath);
        Throws<UnauthorizedAccessException>(() => f.Installer.InstallExtracted(f.Settings, record, new("v2", "release", []), new("source", "", true), f.Package("Foo", "New")));
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        Assert(record.InstalledVersion == null && record.InstallationId == null && record.AdoptedFolders.Count == 1 && record.Origin == "Manual");
        AddonInstaller.ValidateOwnership(f.AddOns, record, "Foo");
    }
    private static void StopManaging()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo"); Manual(f, "Bar");
        var service = new AddonManagement(f.Store);
        var record = service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        service.AssignSource(f.Settings, "Bar", record.RepositoryUrl);
        var before = SafeFiles.HashFiles(f.AddOns, "");
        service.StopManaging(f.Settings, record, "Foo");
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        var inventory = new AddonInventory().Read(f.Settings);
        Assert(inventory.Entries.Single(e => e.FolderName == "Foo").Management == null && record.InstalledFolders.SequenceEqual(["Bar"]));
        service.StopManaging(f.Settings, record, "Bar");
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        Assert(f.Store.Load().Addons.Count == 0 && new AddonInventory().Read(f.Settings).Entries.Count == 2);
        f.Settings.Addons.Add(f.Addon); f.Install("v1", "ManagerInstalled");
        before = SafeFiles.HashFiles(f.AddOns, "");
        service.StopManaging(f.Settings, f.Addon, "ManagerInstalled");
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        Assert(File.Exists(Path.Combine(f.AddOns, "ManagerInstalled", AddonInstaller.MarkerFile)));
        var readopted = service.AssignSource(f.Settings, "ManagerInstalled", "https://github.com/owner/NewSource");
        AddonInstaller.ValidateOwnership(f.AddOns, readopted, "ManagerInstalled");
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
    }
    private static void Reassign()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo"); Manual(f, "Bar");
        var service = new AddonManagement(f.Store);
        var first = service.AssignSource(f.Settings, "Foo", "https://github.com/owner/First");
        service.AssignSource(f.Settings, "Bar", first.RepositoryUrl);
        var before = SafeFiles.HashFiles(f.AddOns, "");
        var second = service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Second");
        Assert(first.InstalledFolders.SequenceEqual(["Bar"]) && second.InstalledFolders.SequenceEqual(["Foo"]));
        Assert(f.Store.Load().Addons.Count == 2);
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
    }
    private static void MultipleWoWPaths()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo");
        var service = new AddonManagement(f.Store);
        var first = service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        var originalWoW = f.Settings.WoWPath;
        var secondWoW = Path.Combine(f.Root, "SecondWoW"); Directory.CreateDirectory(secondWoW);
        File.WriteAllText(Path.Combine(secondWoW, "Wow.exe"), "fixture");
        service.SetWoWPath(f.Settings, secondWoW);
        Assert(new AddonInventory().Read(f.Settings).Entries.Count == 0 && f.Settings.Addons.Contains(first));
        Manual(f, "Foo", "1.0.0");
        var second = service.AssignSource(f.Settings, "Foo", first.RepositoryUrl);
        Assert(first.Id != second.Id && f.Settings.Addons.Count == 2);
        Throws<IOException>(() => f.Installer.Remove(f.Settings, first));
        f.Installer.InstallExtracted(f.Settings, second, new("v2", "release", []), new("source", "", true), f.Package("Foo"));
        service.SetWoWPath(f.Settings, originalWoW);
        Assert(new AddonInventory().Read(f.Store.Load()).Entries.Single().Management?.Id == first.Id);
        Assert(File.ReadAllText(Path.Combine(f.AddOns, "Foo", "main.lua")) == "-- local addon code");
    }
    private static void Migration()
    {
        using var f = new Fixture(); f.Install("v1", "Foo", "Bar");
        var legacy = JsonSerializer.Serialize(new { SchemaVersion = 1, f.Settings.WoWPath, ProtectedGitHubToken = "", Addons = new[]
        { new { f.Addon.Id, f.Addon.RepositoryUrl, f.Addon.Name, f.Addon.InstalledVersion, f.Addon.InstalledVersionKind, f.Addon.InstallationId, f.Addon.InstalledFolders, f.Addon.PreferredAssetName } } });
        File.WriteAllText(f.Store.FilePath, legacy);
        var before = SafeFiles.HashFiles(f.AddOns, "");
        var store = new SettingsStore(f.Store.DirectoryPath);
        var migrated = store.Load();
        Assert(migrated.SchemaVersion == 2 && migrated.Addons.Single().Id == f.Addon.Id && migrated.Addons.Single().AddOnsPath == f.AddOns);
        Assert(File.ReadAllText(store.FilePath) == legacy); // Migration is in-memory until the next successful save.
        foreach (var folder in migrated.Addons.Single().InstalledFolders) AddonInstaller.ValidateOwnership(f.AddOns, migrated.Addons.Single(), folder);
        store.Save(migrated);
        Assert(File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.v1.backup.json")) == legacy);
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        Assert(new AddonInventory().Read(migrated).Entries.Count == 2);
    }
    private static void Missing()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo");
        var record = new AddonManagement(f.Store).AssignSource(f.Settings, "Foo", "https://github.com/owner/Example");
        Directory.Move(Path.Combine(f.AddOns, "Foo"), Path.Combine(f.Root, "manual-backup"));
        var snapshot = new AddonInventory().Read(f.Store.Load());
        Assert(snapshot.Entries.Single() is { IsInstalled: false, FolderName: "Foo" } && snapshot.Entries.Single().Management?.Id == record.Id);
        Assert(f.Store.Load().Addons.Single().InstalledFolders.SequenceEqual(["Foo"]));
    }
    private static void AdoptionFailure()
    {
        using var f = new Fixture(); f.Settings.Addons.Clear(); Manual(f, "Foo");
        Directory.CreateDirectory(f.Store.DirectoryPath); Directory.CreateDirectory(f.Store.FilePath);
        var before = SafeFiles.HashFiles(f.AddOns, "");
        var service = new AddonManagement(f.Store);
        Throws<UnauthorizedAccessException>(() => service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Example"));
        Assert(f.Settings.Addons.Count == 0);
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Throws<OperationCanceledException>(() => service.AssignSource(f.Settings, "Foo", "https://github.com/owner/Example", ct.Token));
        EqualTree(before, SafeFiles.HashFiles(f.AddOns, ""));
    }
}
