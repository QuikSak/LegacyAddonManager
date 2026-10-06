using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

public sealed record GitHubRepository(string Owner, string Name)
{
    public string Slug => $"{Owner}/{Name}";
    public string Url => $"https://github.com/{Slug}";

    public static GitHubRepository Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter an HTTPS repository URL such as https://github.com/ProfetGit/RefactorGear.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2) throw new ArgumentException("Use the repository URL, without /tree, /releases, or other subpages.");
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!Regex.IsMatch(parts[0], @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$") ||
            !Regex.IsMatch(name, @"^[A-Za-z0-9_.-]{1,100}$") || name is "." or "..")
            throw new ArgumentException("The GitHub owner or repository name is invalid.");
        return new(parts[0], name);
    }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public string WoWPath { get; set; } = "";
    public string ProtectedGitHubToken { get; set; } = "";
    public string RcloneConfigPath { get; set; } = "";
    public string RcloneRemoteProfile { get; set; } = "";
    public string ProtectedRclonePassword { get; set; } = "";
    public bool SyncIsPublisher { get; set; }
    public Guid SyncPublisherId { get; set; } = Guid.NewGuid();
    public List<ManagedAddon> Addons { get; set; } = [];
    public Dictionary<string, List<string>> CustomPublishFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ManagedAddon
{
    public Guid? CustomPublisherId { get; set; }
    public string? CustomSourceId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsCustom => CustomPublisherId != null;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RepositoryUrl { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InstalledVersion { get; set; }
    public string? InstalledVersionKind { get; set; }
    public string? InstallationId { get; set; }
    public List<string> InstalledFolders { get; set; } = [];
    // Exact asset name; null selects source archive when multiple release ZIPs exist.
    public string? PreferredAssetName { get; set; }
    public string? AddOnsPath { get; set; }
    public string Origin { get; set; } = "Manager";
    public bool WasDiscoveredManually { get; set; }
    public List<AdoptedFolder> AdoptedFolders { get; set; } = [];
    public Dictionary<string, string> InstalledTocVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FolderDisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AdoptedFolder
{
    public string Name { get; set; } = "";
    // Kept in settings: assigning a source never writes into the addon itself.
    public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DownloadOption(string Name, string Url, bool IsSourceArchive);
public sealed record RepositoryVersion(string Version, string Kind, IReadOnlyList<DownloadOption> Downloads)
{
    public string Display => Kind == "commit" ? Version[..Math.Min(10, Version.Length)] : Version;
}

public sealed class OwnershipManifest
{
    public Guid AddonId { get; set; }
    public string InstallationId { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
