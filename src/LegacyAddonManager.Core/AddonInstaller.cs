using System.Text.Json;

namespace LegacyAddonManager.Core;

public sealed class AddonInstaller(SettingsStore store)
{
    public const string MarkerFile = ".legacy-addon-manager.json";
    public string? CleanupWarning { get; private set; }

    public static string GetAddOnsPath(string wowPath)
    {
        if (string.IsNullOrWhiteSpace(wowPath) || !Directory.Exists(wowPath) || !File.Exists(Path.Combine(wowPath, "Wow.exe")))
            throw new IOException("Choose the WoW installation folder containing Wow.exe.");
        var root = Path.GetFullPath(Path.Combine(wowPath, "Interface", "AddOns"));
        SafeFiles.EnsureNoLinks(root);
        return root;
    }

    public static void ValidateOwnership(string root, ManagedAddon addon, string name)
    {
        var folder = SafeFiles.Child(root, name);
        SafeFiles.EnsureNoLinks(folder);
        if (!Directory.Exists(folder))
        {
            if (File.Exists(folder)) throw new IOException($"A file conflicts with addon folder {name}.");
            return;
        }
        var adopted = addon.AdoptedFolders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (adopted != null)
        {
            ValidateBaseline(folder, name, adopted.Baseline, "");
            return;
        }
        var marker = Path.Combine(folder, MarkerFile);
        SafeFiles.EnsureNoLinks(marker);
        if (!File.Exists(marker)) throw new IOException($"{name} is unmanaged. It will not be overwritten or deleted. Move it out of AddOns yourself first.");
        OwnershipManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<OwnershipManifest>(File.ReadAllText(marker)); }
        catch (JsonException) { throw new IOException($"Ownership information for {name} is invalid. The folder has been preserved."); }
        if (manifest == null || manifest.Files == null || manifest.AddonId != addon.Id || manifest.InstallationId != addon.InstallationId)
            throw new IOException($"{name} belongs to another installation. The folder has been preserved.");
        ValidateBaseline(folder, name, manifest.Files, MarkerFile);
    }

    private static void ValidateBaseline(string folder, string name, Dictionary<string, string> baseline, string excludedName)
    {
        var actual = SafeFiles.HashFiles(folder, excludedName);
        if (actual.Count != baseline.Count || actual.Any(f => !baseline.TryGetValue(f.Key, out var hash) || hash != f.Value))
            throw new IOException($"{name} has changed or contains untracked files. Back up and move that folder out of AddOns before retrying. No files were changed.");
    }

    private static void EnsureScope(AppSettings settings, ManagedAddon addon)
    {
        if (!ManagementPaths.AppliesTo(addon, settings.WoWPath))
            throw new IOException("This source belongs to a different WoW installation. Select its WoW path before changing its addon files.");
    }

    private static void EnsureNoPendingTransaction(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateDirectories(root, ".lam-transaction-*").Any())
            throw new IOException("An interrupted transaction exists in AddOns. Recover its backups using README instructions before continuing.");
    }

    public async Task InstallAsync(AppSettings settings, ManagedAddon addon, RepositoryVersion version,
        DownloadOption download, GitHubService github, IProgress<string>? progress, CancellationToken ct, SyncProfileAddon? expectation = null,
        Action? validateBeforeCommit = null)
    {
        CleanupWarning = null;
        EnsureScope(settings, addon);
        var root = GetAddOnsPath(settings.WoWPath);
        EnsureNoPendingTransaction(root);
        var temporaryRoot = Path.GetTempPath();
        var temporary = SafeFiles.Child(temporaryRoot, ".lam-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var zip = Path.Combine(temporary, "addon.zip");
            await github.DownloadAsync(download.Url, zip, progress, ct);
            await Task.Run(() =>
            {
                progress?.Report("Extracting and validating addon folders…");
                var extracted = Path.Combine(temporary, "extracted");
                AddonArchive.Extract(zip, extracted, ct);
                var folders = AddonArchive.Discover(extracted);
                InstallExtracted(settings, addon, version, download, folders, progress, ct, expectation, validateBeforeCommit);
            }, ct);
        }
        finally
        {
            try { SafeFiles.DeleteOwnedWorkDirectory(temporary, temporaryRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CleanupWarning = $"Temporary files remain at {temporary}."; }
        }
    }

    public void InstallExtracted(AppSettings settings, ManagedAddon addon, RepositoryVersion version,
        DownloadOption download, IReadOnlyList<AddonFolder> folders, IProgress<string>? progress = null, CancellationToken ct = default,
        SyncProfileAddon? expectation = null, Action? validateBeforeCommit = null)
    {
        CleanupWarning = null;
        EnsureScope(settings, addon);
        if (folders.Count == 0 || folders.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Count)
            throw new InvalidDataException("The package must contain uniquely named addon folders.");
        if (expectation != null)
        {
            SyncProfileStore.ValidateAddon(expectation);
            if (version.Version != expectation.Version || version.Kind != expectation.VersionKind ||
                (download.IsSourceArchive ? null : download.Name) != expectation.AssetName ||
                !AddonContent.FoldersEqual(folders.Select(f => f.Name), expectation.Folders) ||
                AddonContent.Hash(folders, ct) != expectation.ContentHash)
                throw new InvalidDataException("The downloaded addon contents differ from the published profile. Installed files were preserved. Ask the publisher to verify and republish the profile.");
        }
        var root = GetAddOnsPath(settings.WoWPath);
        EnsureNoPendingTransaction(root);
        Directory.CreateDirectory(root);
        foreach (var name in addon.InstalledFolders) ValidateOwnership(root, addon, name);
        foreach (var folder in folders)
        {
            var target = SafeFiles.Child(root, folder.Name);
            if (settings.Addons.Any(a => a.Id != addon.Id && ManagementPaths.AppliesTo(a, settings.WoWPath) && a.InstalledFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase)))
                throw new IOException($"{folder.Name} is managed by another repository.");
            if (!addon.InstalledFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase) && (Directory.Exists(target) || File.Exists(target)))
                throw new IOException($"{folder.Name} already exists and is unmanaged. It will not be overwritten.");
            if (File.Exists(Path.Combine(folder.SourcePath, MarkerFile))) throw new InvalidDataException("Package contains a reserved ownership file.");
        }

        var work = SafeFiles.Child(root, ".lam-transaction-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(work, "staged");
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        var installationId = Guid.NewGuid().ToString("N");
        var oldVersion = addon.InstalledVersion;
        var oldKind = addon.InstalledVersionKind;
        var oldId = addon.InstallationId;
        var oldFolders = addon.InstalledFolders;
        var oldAsset = addon.PreferredAssetName;
        var oldAdopted = addon.AdoptedFolders;
        var oldPath = addon.AddOnsPath;
        var oldOrigin = addon.Origin;
        var oldTocVersions = addon.InstalledTocVersions;
        var oldDisplayNames = addon.FolderDisplayNames;
        var moved = new List<string>();
        var installed = new List<string>();
        var preserveWork = false;
        try
        {
            File.WriteAllText(Path.Combine(work, "recovery.json"), JsonSerializer.Serialize(new
            {
                Operation = "Install", AddonId = addon.Id, OldInstallationId = oldId, NewInstallationId = installationId,
                OldFolders = oldFolders, NewFolders = folders.Select(f => f.Name), OldVersion = oldVersion,
                SettingsBefore = settings
            }, SettingsStore.JsonOptions));
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var destination = SafeFiles.Child(stage, folder.Name);
                SafeFiles.CopyDirectory(folder.SourcePath, destination);
                var manifest = new OwnershipManifest { AddonId = addon.Id, InstallationId = installationId, Files = SafeFiles.HashFiles(destination, MarkerFile) };
                File.WriteAllText(Path.Combine(destination, MarkerFile), JsonSerializer.Serialize(manifest, SettingsStore.JsonOptions));
            }
            ct.ThrowIfCancellationRequested();
            // Revalidate immediately before touching the installation. Cancellation ends before the transaction starts.
            validateBeforeCommit?.Invoke();
            foreach (var name in oldFolders) ValidateOwnership(root, addon, name);
            progress?.Report("Installing addon folders…");
            foreach (var name in oldFolders)
            {
                var existing = SafeFiles.Child(root, name);
                if (!Directory.Exists(existing)) continue;
                Directory.Move(existing, SafeFiles.Child(backup, name));
                moved.Add(name);
            }
            foreach (var folder in folders)
            {
                var target = SafeFiles.Child(root, folder.Name);
                SafeFiles.EnsureNoLinks(target);
                Directory.Move(SafeFiles.Child(stage, folder.Name), target);
                installed.Add(folder.Name);
            }
            addon.InstalledVersion = version.Version;
            addon.InstalledVersionKind = version.Kind;
            addon.InstallationId = installationId;
            addon.InstalledFolders = folders.Select(f => f.Name).ToList();
            addon.PreferredAssetName = download.IsSourceArchive ? null : download.Name;
            addon.AddOnsPath = root;
            addon.Origin = addon.IsCustom ? "Custom sync" : "Manager";
            addon.AdoptedFolders = [];
            addon.InstalledTocVersions = new(StringComparer.OrdinalIgnoreCase);
            addon.FolderDisplayNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (var name in addon.InstalledFolders)
            {
                var metadata = TocReader.ReadFolder(SafeFiles.Child(root, name)).Metadata;
                addon.InstalledTocVersions[name] = metadata.Version;
                addon.FolderDisplayNames[name] = string.IsNullOrWhiteSpace(metadata.Title) ? name : metadata.Title;
            }
            store.Save(settings);
        }
        catch (Exception original)
        {
            addon.InstalledVersion = oldVersion;
            addon.InstalledVersionKind = oldKind;
            addon.InstallationId = oldId;
            addon.InstalledFolders = oldFolders;
            addon.PreferredAssetName = oldAsset;
            addon.AdoptedFolders = oldAdopted;
            addon.AddOnsPath = oldPath;
            addon.Origin = oldOrigin;
            addon.InstalledTocVersions = oldTocVersions;
            addon.FolderDisplayNames = oldDisplayNames;
            try
            {
                foreach (var name in installed)
                {
                    var target = SafeFiles.Child(root, name);
                    var temporaryAddon = new ManagedAddon { Id = addon.Id, InstallationId = installationId };
                    ValidateOwnership(root, temporaryAddon, name);
                    if (Directory.Exists(target)) Directory.Move(target, SafeFiles.Child(stage, name));
                }
                foreach (var name in moved) Directory.Move(SafeFiles.Child(backup, name), SafeFiles.Child(root, name));
            }
            catch (Exception rollback)
            {
                preserveWork = true;
                throw new IOException($"Installation failed and rollback could not finish. Backups are preserved at {work}. {rollback.Message}", original);
            }
            throw;
        }
        finally { if (!preserveWork) Cleanup(work, root); }
    }

    public void Remove(AppSettings settings, ManagedAddon addon)
    {
        CleanupWarning = null;
        EnsureScope(settings, addon);
        if (addon.InstalledFolders.Count == 0)
        {
            var index = settings.Addons.IndexOf(addon);
            settings.Addons.Remove(addon);
            try { store.Save(settings); }
            catch { settings.Addons.Insert(index, addon); throw; }
            return;
        }
        var root = GetAddOnsPath(settings.WoWPath);
        EnsureNoPendingTransaction(root);
        foreach (var name in addon.InstalledFolders) ValidateOwnership(root, addon, name);
        Directory.CreateDirectory(root);
        var work = SafeFiles.Child(root, ".lam-transaction-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        var position = settings.Addons.IndexOf(addon);
        var preserveWork = false;
        try
        {
            File.WriteAllText(Path.Combine(work, "recovery.json"), JsonSerializer.Serialize(new { Operation = "Remove", AddonId = addon.Id, SettingsBefore = settings }, SettingsStore.JsonOptions));
            foreach (var name in addon.InstalledFolders)
            {
                var folder = SafeFiles.Child(root, name);
                if (!Directory.Exists(folder)) continue;
                Directory.Move(folder, SafeFiles.Child(backup, name));
                moved.Add(name);
            }
            settings.Addons.Remove(addon);
            store.Save(settings);
        }
        catch (Exception original)
        {
            if (!settings.Addons.Contains(addon)) settings.Addons.Insert(position, addon);
            try { foreach (var name in moved) Directory.Move(SafeFiles.Child(backup, name), SafeFiles.Child(root, name)); }
            catch (Exception rollback)
            {
                preserveWork = true;
                throw new IOException($"Removal failed and rollback could not finish. Backups are preserved at {work}. {rollback.Message}", original);
            }
            throw;
        }
        finally { if (!preserveWork) Cleanup(work, root); }
    }

    private void Cleanup(string work, string root)
    {
        try { SafeFiles.DeleteOwnedWorkDirectory(work, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanupWarning = $"Operation completed, but transaction files remain at {work}. See README recovery instructions before the next install or removal.";
        }
    }
}
