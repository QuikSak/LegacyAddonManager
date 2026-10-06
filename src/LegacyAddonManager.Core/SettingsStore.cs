using System.Text.Json;

namespace LegacyAddonManager.Core;

public sealed class SettingsStore
{
    private bool needsMigrationBackup;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsStore(string? directory = null) => DirectoryPath = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWAddonManager");

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new InvalidDataException("Settings are empty.");
            if (settings.SchemaVersion is not (1 or 2) || settings.Addons is null || settings.WoWPath is null || settings.ProtectedGitHubToken is null ||
                settings.CustomPublishFolders is null || settings.SyncPublisherId == Guid.Empty || settings.RcloneConfigPath is null ||
                settings.RcloneRemoteProfile is null || settings.ProtectedRclonePassword is null ||
                (settings.RcloneConfigPath.Length > 0 && !Path.IsPathFullyQualified(settings.RcloneConfigPath)))
                throw new InvalidDataException("Unsupported or incomplete settings.");
            if (settings.RcloneRemoteProfile.Length > 0)
            {
                RcloneProfileTransport.ValidateRemotePath(settings.RcloneRemoteProfile);
                if (!Path.IsPathFullyQualified(settings.RcloneConfigPath))
                    throw new InvalidDataException("Invalid rclone connection settings.");
            }
            if (settings.SchemaVersion == 1)
            {
                foreach (var addon in settings.Addons)
                    if (addon != null) addon.AddOnsPath = ManagementPaths.ForWoW(settings.WoWPath);
                settings.SchemaVersion = 2;
                needsMigrationBackup = true;
            }
            if (settings.Addons.Any(a => a is null || a.Id == Guid.Empty || a.Name is null || a.RepositoryUrl is null || a.InstalledFolders is null || a.AdoptedFolders is null ||
                a.InstalledTocVersions is null || a.FolderDisplayNames is null ||
                (a.InstalledVersion != null && (string.IsNullOrEmpty(a.InstallationId) || a.InstalledFolders.Count == 0))) ||
                settings.Addons.Select(a => a.Id).Distinct().Count() != settings.Addons.Count)
                throw new InvalidDataException("Invalid managed addon records.");
            foreach (var addon in settings.Addons)
            {
                if (addon.IsCustom)
                {
                    if (addon.CustomPublisherId == Guid.Empty || addon.CustomSourceId == null ||
                        !System.Text.RegularExpressions.Regex.IsMatch(addon.CustomSourceId, "^[A-F0-9]{64}$") ||
                        addon.RepositoryUrl != "" || addon.InstalledVersionKind != "custom" || addon.InstalledVersion == null ||
                        !System.Text.RegularExpressions.Regex.IsMatch(addon.InstalledVersion, "^[A-F0-9]{64}$"))
                        throw new InvalidDataException("Invalid custom addon sync record.");
                }
                else
                {
                    if (addon.CustomSourceId != null) throw new InvalidDataException("Invalid custom addon identity.");
                    addon.RepositoryUrl = GitHubRepository.Parse(addon.RepositoryUrl).Url;
                }
                foreach (var folder in addon.InstalledFolders) SafeFiles.ValidateName(folder);
                if (addon.AddOnsPath != null && (!Path.IsPathFullyQualified(addon.AddOnsPath) || !Path.GetFullPath(addon.AddOnsPath).TrimEnd(Path.DirectorySeparatorChar).Equals(addon.AddOnsPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Invalid installation path.");
                if (addon.InstalledFolders.Count > 0 && addon.AddOnsPath == null)
                    throw new InvalidDataException("Installed folder associations must be bound to a WoW installation.");
                if (addon.AddOnsPath != null) addon.AddOnsPath = Path.GetFullPath(addon.AddOnsPath).TrimEnd(Path.DirectorySeparatorChar);
                if (addon.InstalledFolders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != addon.InstalledFolders.Count ||
                    addon.AdoptedFolders.Any(f => f is null || f.Baseline is null || !addon.InstalledFolders.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) ||
                    addon.AdoptedFolders.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != addon.AdoptedFolders.Count ||
                    (addon.InstallationId == null && addon.InstalledFolders.Count != addon.AdoptedFolders.Count))
                    throw new InvalidDataException("Invalid installed folder list.");
            }
            foreach (var preference in settings.CustomPublishFolders)
            {
                if (!Path.IsPathFullyQualified(preference.Key) || preference.Value == null)
                    throw new InvalidDataException("Invalid custom publish selection.");
                foreach (var folder in preference.Value) SafeFiles.ValidateName(folder);
            }
            settings.CustomPublishFolders = new(settings.CustomPublishFolders, StringComparer.OrdinalIgnoreCase);
            var associations = settings.Addons.SelectMany(a => a.InstalledFolders.Select(f => (a.AddOnsPath ?? "") + "|" + f)).ToArray();
            if (associations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != associations.Length)
                throw new InvalidDataException("An addon folder is associated with multiple repositories.");
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot read {FilePath}. Your settings have been preserved. Restore a valid settings file before starting the app.", ex);
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (needsMigrationBackup && File.Exists(FilePath))
        {
            var backup = Path.Combine(DirectoryPath, "settings.v1.backup.json");
            if (File.Exists(backup)) backup = Path.Combine(DirectoryPath, $"settings.v1.backup-{Guid.NewGuid():N}.json");
            File.Copy(FilePath, backup, false);
            needsMigrationBackup = false;
        }
        var temporary = Path.Combine(DirectoryPath, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
