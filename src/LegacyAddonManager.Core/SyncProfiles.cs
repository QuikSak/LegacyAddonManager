using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

// Portable desired state. Machine paths, tokens, ownership IDs and saved variables stay local.
public sealed class SyncProfile
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public Guid PublisherId { get; set; }
    [JsonRequired] public Guid Revision { get; set; } = Guid.NewGuid();
    [JsonRequired] public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired] public List<SyncProfileAddon> Addons { get; set; } = [];
}

public sealed class SyncProfileAddon
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CustomSourceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ArchiveHash { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonIgnore] public bool IsCustom => VersionKind == "custom";
    [JsonIgnore] public string DisplayName => IsCustom ? Name ?? Folders.FirstOrDefault() ?? "Custom addon" : RepositoryUrl;
    [JsonRequired] public string RepositoryUrl { get; set; } = "";
    [JsonRequired] public string Version { get; set; } = "";
    [JsonRequired] public string VersionKind { get; set; } = "";
    // Null explicitly means source ZIP, even if a release has a single downloadable asset.
    public string? AssetName { get; set; }
    [JsonRequired] public List<string> Folders { get; set; } = [];
    [JsonRequired] public string ContentHash { get; set; } = "";
}

public sealed record ProfileSnapshot(SyncProfile Profile, string FileHash);
public sealed record ProfileExport(SyncProfile Profile, IReadOnlyList<string> Excluded)
{
    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>();
}
public sealed record SyncPlanItem(SyncProfileAddon Target, ManagedAddon? Local, string Action, string Detail);
public sealed record SyncPlan(IReadOnlyList<SyncPlanItem> Items, IReadOnlyList<ManagedAddon> Extras, int UnmanagedCount)
{
    public int Changes => Items.Count(i => i.Action != "Current");
}

public static class AddonContent
{
    public static bool FoldersEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(right.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static string Hash(IEnumerable<AddonFolder> folders, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Add(folder.Name.ToUpperInvariant());
            foreach (var file in SafeFiles.HashFiles(folder.SourcePath, AddonInstaller.MarkerFile, ct).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                Add(file.Key.Replace('\\', '/').ToUpperInvariant());
                Add(file.Value);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData(new byte[] { 0 });
        }
    }
}

public sealed class SyncProfileStore(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public ProfileSnapshot Read()
    {
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 2 * 1024 * 1024) throw new InvalidDataException("Sync profile exceeds the 2 MB limit.");
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = buffer.ToArray();
        try
        {
            var profile = JsonSerializer.Deserialize<SyncProfile>(bytes, Options) ?? throw new InvalidDataException("Sync profile is empty.");
            Validate(profile);
            return new(profile, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new InvalidDataException("The sync profile is invalid or uses an unsupported format. Its contents were preserved.", ex); }
    }

    public void EnsureUnchanged(string? expectedHash)
    {
        var actual = File.Exists(FilePath) ? Read().FileHash : null;
        if (actual != expectedHash)
            throw new IOException("The profile changed. Preview it again before publishing or syncing.");
    }

    public void Publish(SyncProfile profile, string? expectedHash)
    {
        Validate(profile);
        var parent = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The local profile staging directory is missing.");
        // A local lock serializes this app's writers; optimistic comparison catches changes since preview.
        var lockPath = FilePath + ".lock";
        using var profileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        EnsureUnchanged(expectedHash);
        if (File.Exists(FilePath) && Read().Profile.PublisherId != profile.PublisherId)
            throw new IOException("This profile belongs to another publisher. Connect as a follower, or use a new remote profile filename.");
        var temp = Path.Combine(parent, ".lam-profile-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, profile, Options);
                if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Sync profile exceeds the 2 MB limit.");
                stream.Flush(true);
            }
            EnsureUnchanged(expectedHash);
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Validate(SyncProfile profile)
    {
        if (profile.SchemaVersion is not (1 or 2) || profile.PublisherId == Guid.Empty || profile.Revision == Guid.Empty || profile.PublishedAt == default ||
            profile.Addons == null || profile.Addons.Count > 500)
            throw new InvalidDataException("Unsupported or incomplete sync profile. A profile supports up to 500 installed source groups.");
        foreach (var addon in profile.Addons) ValidateAddon(addon);
        if (profile.SchemaVersion == 1 && profile.Addons.Any(a => a.IsCustom)) throw new InvalidDataException("Custom addon packages require profile schema 2. Update Legacy Addon Manager on both computers.");
        if (profile.Addons.Select(a => a.IsCustom ? "custom:" + a.CustomSourceId : GitHubRepository.Parse(a.RepositoryUrl).Url).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Addons.Count ||
            profile.Addons.SelectMany(a => a.Folders).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Addons.Sum(a => a.Folders.Count))
            throw new InvalidDataException("The sync profile contains duplicate repositories or conflicting addon folders.");
    }

    public static void ValidateAddon(SyncProfileAddon addon)
    {
        if (addon == null || addon.RepositoryUrl == null || addon.Version == null || addon.VersionKind == null || addon.ContentHash == null ||
            addon.Folders == null || addon.Folders.Count is < 1 or > 200 ||
            addon.Version.Length is < 1 or > 256 || addon.Version.Any(char.IsControl) ||
            addon.VersionKind is not ("release" or "commit" or "custom") ||
            (addon.VersionKind == "commit" && (!Regex.IsMatch(addon.Version, "^[a-fA-F0-9]{40}$") || addon.AssetName != null)) ||
            !Regex.IsMatch(addon.ContentHash, "^[A-F0-9]{64}$"))
            throw new InvalidDataException("Invalid version, folders, or content hash in sync profile.");
        if (addon.IsCustom)
        {
            if (addon.RepositoryUrl != "" || addon.AssetName != null || addon.CustomSourceId == null || addon.ArchiveHash == null ||
                addon.Name == null || addon.Name.Length is < 1 or > 256 || addon.Name.Any(char.IsControl) ||
                !Regex.IsMatch(addon.CustomSourceId, "^[A-F0-9]{64}$") || !Regex.IsMatch(addon.ArchiveHash, "^[A-F0-9]{64}$") || addon.Version != addon.ContentHash)
                throw new InvalidDataException("Invalid custom addon package identity or checksum.");
        }
        else
        {
            if (addon.CustomSourceId != null || addon.ArchiveHash != null || addon.Name != null) throw new InvalidDataException("GitHub entries cannot contain custom package metadata.");
            addon.RepositoryUrl = GitHubRepository.Parse(addon.RepositoryUrl).Url;
        }
        foreach (var folder in addon.Folders)
        {
            if (folder == null || folder.Length > 255 || folder.StartsWith(".lam-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid addon folder in sync profile.");
            SafeFiles.ValidateName(folder);
        }
        if (addon.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != addon.Folders.Count)
            throw new InvalidDataException("Duplicate addon folders in sync profile.");
        if (addon.AssetName != null)
        {
            SafeFiles.ValidateName(addon.AssetName);
            if (addon.AssetName.Length > 255 || !addon.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The profile asset must be a ZIP filename.");
        }
    }
}

public sealed class ProfileSyncService(SettingsStore settingsStore)
{
    public ProfileExport Export(AppSettings settings, CancellationToken ct = default, IReadOnlyCollection<string>? customFolders = null, string? packageDirectory = null)
    {
        var root = AddonInstaller.GetAddOnsPath(settings.WoWPath);
        var profile = new SyncProfile { PublisherId = settings.SyncPublisherId };
        var excluded = new List<string>();
        var candidates = customFolders?.Count > 0 ? CustomAddonPackages.Candidates(settings, ct)
            .Where(c => c.Folders.Any(f => customFolders.Contains(f, StringComparer.OrdinalIgnoreCase))).ToArray() : [];
        var selected = candidates.SelectMany(c => c.Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (customFolders != null && customFolders.Any(f => !selected.Contains(f))) throw new IOException("A selected custom addon is missing. Refresh addons and select it again.");
        var packages = new Dictionary<string, string>();
        if (candidates.Length > 0)
        {
            if (packageDirectory == null) throw new ArgumentException("Custom publication needs a temporary package directory.");
            SafeFiles.EnsureNoLinks(packageDirectory);
            Directory.CreateDirectory(packageDirectory);
            profile.SchemaVersion = 2;
            foreach (var candidate in candidates)
            {
                var package = CustomAddonPackages.Create(root, candidate, packageDirectory, ct);
                profile.Addons.Add(package.Addon); packages.Add(package.Addon.ArchiveHash!, package.ZipPath);
            }
        }
        foreach (var record in Active(settings))
        {
            ct.ThrowIfCancellationRequested();
            if (record.InstalledFolders.Any(selected.Contains)) continue;
            if (record.IsCustom) { excluded.Add(record.Name + " — custom files not selected for upload"); continue; }
            if (record.InstalledVersion == null || record.InstalledVersionKind is not ("release" or "commit") || record.AdoptedFolders.Count > 0 ||
                record.InstalledFolders.Count == 0 || record.InstalledFolders.Any(f => !Directory.Exists(SafeFiles.Child(root, f))))
            { excluded.Add(record.Name + " — missing, not installed, or GitHub version not established"); continue; }
            foreach (var folder in record.InstalledFolders) AddonInstaller.ValidateOwnership(root, record, folder);
            profile.Addons.Add(new()
            {
                RepositoryUrl = GitHubRepository.Parse(record.RepositoryUrl).Url, Version = record.InstalledVersion,
                VersionKind = record.InstalledVersionKind, AssetName = record.PreferredAssetName,
                Folders = record.InstalledFolders.ToList(), ContentHash = LocalHash(root, record.InstalledFolders, ct)
            });
        }
        profile.Addons = profile.Addons.OrderBy(a => a.RepositoryUrl, StringComparer.OrdinalIgnoreCase).ToList();
        if (profile.Addons.Count == 0 && excluded.Count > 0 && customFolders == null)
            throw new IOException("No installed addons have a publishable GitHub version. Install or use Update Selected for an adopted source first, then publish again.");
        SyncProfileStore.Validate(profile);
        return new(profile, excluded) { Packages = packages };
    }

    public SyncPlan Preview(AppSettings settings, SyncProfile profile, CancellationToken ct = default)
    {
        SyncProfileStore.Validate(profile);
        var root = AddonInstaller.GetAddOnsPath(settings.WoWPath);
        var active = Active(settings).ToArray();
        var items = new List<SyncPlanItem>();
        foreach (var target in profile.Addons)
        {
            ct.ThrowIfCancellationRequested();
            var matches = active.Where(a => Matches(a, target, profile.PublisherId)).ToArray();
            if (matches.Length > 1) throw new IOException("Multiple local records share a repository. Resolve their associations before syncing.");
            var local = matches.SingleOrDefault();
            if (local != null)
                foreach (var folder in local.InstalledFolders) AddonInstaller.ValidateOwnership(root, local, folder);
            foreach (var folder in target.Folders)
            {
                if (active.Any(a => a != local && a.InstalledFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException($"{folder} is associated with another local source. Its files were preserved.");
                if (local?.InstalledFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) != true &&
                    (Directory.Exists(SafeFiles.Child(root, folder)) || File.Exists(SafeFiles.Child(root, folder))))
                    throw new IOException($"{folder} already exists and is unmanaged. Assign its matching source or move it out of AddOns before syncing.");
            }
            var complete = local != null && target.Folders.All(f => Directory.Exists(SafeFiles.Child(root, f)));
            var current = complete && local!.AdoptedFolders.Count == 0 && local.InstalledVersion == target.Version && local.InstalledVersionKind == target.VersionKind &&
                local.PreferredAssetName == target.AssetName && AddonContent.FoldersEqual(local.InstalledFolders, target.Folders) &&
                LocalHash(root, target.Folders, ct) == target.ContentHash;
            var action = current ? "Current" : local == null || local.InstalledFolders.Count == 0 ? "Install" : "Replace / restore";
            var detail = target.IsCustom ? $"Custom files from the publisher · {target.ContentHash[..12]}\nFolders: {string.Join(", ", target.Folders)}"
                : $"{local?.InstalledVersion ?? "not established"} → {target.Version}\nFolders: {string.Join(", ", target.Folders)}";
            if (local?.AdoptedFolders.Count > 0) detail += "\nFirst manager update: replaces adopted files.";
            if (local != null && local.InstalledFolders.Except(target.Folders, StringComparer.OrdinalIgnoreCase).Any())
                detail += "\nOld associated folders removed: " + string.Join(", ", local.InstalledFolders.Except(target.Folders, StringComparer.OrdinalIgnoreCase));
            items.Add(new(target, local, action, detail));
        }
        var extras = active.Where(a => !profile.Addons.Any(p => Matches(a, p, profile.PublisherId))).ToArray();
        var unmanaged = new AddonInventory().Read(settings, ct).Entries.Count(e => e.Local != null && e.Management == null);
        return new(items, extras, unmanaged);
    }

    public async Task<int> ApplyAsync(AppSettings settings, SyncProfileStore profileStore, ProfileSnapshot snapshot, bool removeExtras,
        GitHubService github, IProgress<string>? progress, CancellationToken ct, RcloneProfileTransport? customTransport = null)
    {
        profileStore.EnsureUnchanged(snapshot.FileHash);
        var plan = await Task.Run(() => Preview(settings, snapshot.Profile, ct), ct);
        if (plan.Items.Any(i => i.Target.IsCustom && i.Action != "Current") && customTransport == null)
            throw new IOException("Custom addon files require an rclone connection. Update Legacy Addon Manager on both computers.");
        if (removeExtras)
        {
            var root = AddonInstaller.GetAddOnsPath(settings.WoWPath);
            foreach (var record in plan.Extras)
                foreach (var folder in record.InstalledFolders) AddonInstaller.ValidateOwnership(root, record, folder);
        }
        var installer = new AddonInstaller(settingsStore);
        var completed = 0;
        foreach (var item in plan.Items.Where(i => i.Action != "Current"))
        {
            ct.ThrowIfCancellationRequested();
            profileStore.EnsureUnchanged(snapshot.FileHash);
            progress?.Report($"Syncing {item.Target.DisplayName}…");
            var record = item.Local ?? (item.Target.IsCustom ? new ManagedAddon
            {
                Name = item.Target.Name!, CustomPublisherId = snapshot.Profile.PublisherId, CustomSourceId = item.Target.CustomSourceId,
                AddOnsPath = ManagementPaths.ForWoW(settings.WoWPath)
            } : new ManagedAddon { Name = GitHubRepository.Parse(item.Target.RepositoryUrl).Name, RepositoryUrl = item.Target.RepositoryUrl, AddOnsPath = ManagementPaths.ForWoW(settings.WoWPath) });
            if (item.Local == null) settings.Addons.Add(record);
            string? customCleanupWarning = null;
            try
            {
                if (item.Target.IsCustom)
                {
                    var work = SafeFiles.Child(Path.GetTempPath(), ".lam-custom-sync-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    try
                    {
                        var zip = Path.Combine(work, "addon.zip");
                        await customTransport!.DownloadArchiveAsync(item.Target.ArchiveHash!, zip, ct);
                        await Task.Run(() =>
                        {
                            var folders = CustomAddonPackages.ExtractVerified(zip, Path.Combine(work, "extracted"), item.Target, ct);
                            installer.InstallExtracted(settings, record, new(item.Target.Version, "custom", []), new("Custom files", "", true), folders, progress, ct,
                                item.Target, () => profileStore.EnsureUnchanged(snapshot.FileHash));
                        }, ct);
                    }
                    finally
                    {
                        try { SafeFiles.DeleteOwnedWorkDirectory(work, Path.GetTempPath()); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { customCleanupWarning = $"Temporary custom addon files remain at {work}."; }
                    }
                }
                else
                {
                    var (version, download) = await github.GetPinnedAsync(item.Target, ct);
                    await installer.InstallAsync(settings, record, version, download, github, progress, ct, item.Target,
                        () => profileStore.EnsureUnchanged(snapshot.FileHash));
                }
            }
            catch { if (item.Local == null) settings.Addons.Remove(record); throw; }
            completed++;
            if (installer.CleanupWarning != null) throw new IOException(installer.CleanupWarning);
            if (customCleanupWarning != null) throw new IOException(customCleanupWarning);
        }
        // Optional removal happens only after every requested install succeeded.
        if (removeExtras)
            foreach (var record in plan.Extras)
            {
                ct.ThrowIfCancellationRequested();
                profileStore.EnsureUnchanged(snapshot.FileHash);
                installer.Remove(settings, record);
                completed++;
                if (installer.CleanupWarning != null) throw new IOException(installer.CleanupWarning);
            }
        return completed;
    }

    private static IEnumerable<ManagedAddon> Active(AppSettings settings) => settings.Addons.Where(a => ManagementPaths.AppliesTo(a, settings.WoWPath));
    private static bool Matches(ManagedAddon local, SyncProfileAddon target, Guid publisher) => target.IsCustom
        ? local.IsCustom && local.CustomPublisherId == publisher && local.CustomSourceId == target.CustomSourceId
        : !local.IsCustom && local.RepositoryUrl.Equals(GitHubRepository.Parse(target.RepositoryUrl).Url, StringComparison.OrdinalIgnoreCase);
    private static string LocalHash(string root, IEnumerable<string> folders, CancellationToken ct) =>
        AddonContent.Hash(folders.Select(f => new AddonFolder(SafeFiles.Child(root, f), f)), ct);
}
