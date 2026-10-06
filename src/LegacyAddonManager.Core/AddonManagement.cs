using System.Text.Json;

namespace LegacyAddonManager.Core;

public sealed class AddonManagement(SettingsStore store)
{
    // Transactions here change JSON only. Addon file trees are read, never written.
    public ManagedAddon AssignSource(AppSettings settings, string folderName, string sourceUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var repository = GitHubRepository.Parse(sourceUrl);
        var root = AddonInstaller.GetAddOnsPath(settings.WoWPath);
        var folder = SafeFiles.Child(root, folderName);
        var (metadata, _) = TocReader.ReadFolder(folder);
        var snapshot = SafeFiles.HashFiles(folder, "", ct);
        ct.ThrowIfCancellationRequested();
        var active = settings.Addons.Where(a => ManagementPaths.AppliesTo(a, settings.WoWPath)).ToArray();
        var old = active.FirstOrDefault(a => a.InstalledFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase));
        if (old != null && old.RepositoryUrl.Equals(repository.Url, StringComparison.OrdinalIgnoreCase)) return old;
        var before = CloneRecords(settings);
        try
        {
            Detach(settings, old, folderName);
            var record = settings.Addons.FirstOrDefault(a => ManagementPaths.AppliesTo(a, settings.WoWPath) && a.RepositoryUrl.Equals(repository.Url, StringComparison.OrdinalIgnoreCase));
            if (record == null)
            {
                record = new() { Name = repository.Name, RepositoryUrl = repository.Url, AddOnsPath = root, Origin = "Manual", WasDiscoveredManually = true };
                settings.Addons.Add(record);
            }
            record.AddOnsPath ??= root;
            record.InstalledFolders.Add(folderName);
            record.AdoptedFolders.Add(new() { Name = folderName, Baseline = snapshot });
            record.InstalledTocVersions[folderName] = metadata.Version;
            record.FolderDisplayNames[folderName] = string.IsNullOrWhiteSpace(metadata.Title) ? folderName : metadata.Title;
            record.WasDiscoveredManually = true;
            record.Origin = record.InstalledFolders.Count == record.AdoptedFolders.Count ? "Manual" : "Mixed";
            store.Save(settings);
            return record;
        }
        catch { settings.Addons = before; throw; }
    }

    public void StopManaging(AppSettings settings, ManagedAddon record, string? folderName)
    {
        var before = CloneRecords(settings);
        try
        {
            if (folderName == null) settings.Addons.Remove(record);
            else Detach(settings, record, folderName);
            store.Save(settings);
        }
        catch { settings.Addons = before; throw; }
    }

    public ManagedAddon AddRepository(AppSettings settings, string sourceUrl)
    {
        var repository = GitHubRepository.Parse(sourceUrl);
        if (settings.Addons.Any(a => ManagementPaths.AppliesTo(a, settings.WoWPath) && a.RepositoryUrl.Equals(repository.Url, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("This repository already has an entry for the selected WoW installation. Assign its source to additional folders with Edit Source.");
        var record = new ManagedAddon { Name = repository.Name, RepositoryUrl = repository.Url, AddOnsPath = ManagementPaths.ForWoW(settings.WoWPath) };
        settings.Addons.Add(record);
        try { store.Save(settings); } catch { settings.Addons.Remove(record); throw; }
        return record;
    }

    public void SetWoWPath(AppSettings settings, string wowPath)
    {
        _ = AddonInstaller.GetAddOnsPath(wowPath);
        var previous = settings.WoWPath;
        settings.WoWPath = Path.GetFullPath(wowPath).TrimEnd(Path.DirectorySeparatorChar);
        try { store.Save(settings); } catch { settings.WoWPath = previous; throw; }
    }

    private static void Detach(AppSettings settings, ManagedAddon? record, string folderName)
    {
        if (record == null) return;
        record.InstalledFolders.RemoveAll(f => f.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        record.AdoptedFolders.RemoveAll(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        record.InstalledTocVersions.Remove(folderName);
        record.FolderDisplayNames.Remove(folderName);
        if (record.InstalledFolders.Count == 0) settings.Addons.Remove(record);
        else record.Origin = record.AdoptedFolders.Count == 0 ? "Manager" : record.AdoptedFolders.Count == record.InstalledFolders.Count ? "Manual" : "Mixed";
    }

    private static List<ManagedAddon> CloneRecords(AppSettings settings) => JsonSerializer.Deserialize<List<ManagedAddon>>(
        JsonSerializer.Serialize(settings.Addons, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}
