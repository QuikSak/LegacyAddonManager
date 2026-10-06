using LegacyAddonManager.Core;

namespace LegacyAddonManager.ViewModels;

public sealed class AddonViewModel(InventoryEntry entry) : ObservableObject
{
    public InventoryEntry Entry { get; } = entry;
    public ManagedAddon? Model => Entry.Management;
    public string Name => Entry.DisplayName;
    public string Folder => Entry.FolderName ?? "—";
    public bool IsInstalled => Entry.IsInstalled;
    public bool IsManaged => Model != null;
    public string Source => Model?.IsCustom == true ? "Custom files (rclone)" : Model?.RepositoryUrl ?? "";
    public string InstalledVersion => !IsInstalled ? "—" : !string.IsNullOrWhiteSpace(Entry.Local?.Metadata.Version)
        ? Entry.Local.Metadata.Version : Model?.InstalledVersion is { } version
            ? (Model.InstalledVersionKind is "commit" or "custom" ? version[..Math.Min(10, version.Length)] : version) : "Unknown";
    public string VersionTooltip => $"TOC version: {Entry.Local?.Metadata.Version ?? "Unknown"}\n{(Model?.IsCustom == true ? "Custom content hash" : "Tracked GitHub version")}: {Model?.InstalledVersion ?? "Unknown"}";
    public string MetadataSummary
    {
        get
        {
            var metadata = Entry.Local?.Metadata;
            string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
            return $"Folder: {Folder}\nTOC: {Value(Entry.Local?.TocFile)}\nNotes: {Value(metadata?.Notes)}\nAuthor: {Value(metadata?.Author)}\nInterface: {Value(metadata?.Interface)}\nSavedVariables: {Value(metadata?.SavedVariables)}\nSavedVariablesPerCharacter: {Value(metadata?.SavedVariablesPerCharacter)}\nDependencies: {Value(metadata?.Dependencies)}\nOptionalDependencies: {Value(metadata?.OptionalDependencies)}\nSource: {Value(Source)}\n{(Model?.IsCustom == true ? "Custom content hash" : "Tracked GitHub version")}: {Value(Model?.InstalledVersion)}\nOrigin: {(Model == null ? "Discovered manually" : Model.Origin)}\nAssociated folders: {Value(Model == null ? null : string.Join(", ", Model.InstalledFolders))}";
        }
    }
    private RepositoryVersion? latest;
    public RepositoryVersion? Latest { get => latest; set { Set(ref latest, value); Notify(nameof(LatestVersion)); } }
    public string LatestVersion => Latest?.Display ?? "—";
    private string status = "Installed - Unmanaged";
    public string Status { get => status; set => Set(ref status, value); }
    private string detail = "Assign a GitHub source to begin managing this addon.";
    public string Detail { get => detail; set => Set(ref detail, value); }
    public bool HasUpdate => Status == "Update Available";
    public void RefreshStatus()
    {
        if (Entry.Local?.Error is { } error) { Status = "Error"; Detail = error; return; }
        if (!IsInstalled)
        {
            Status = Entry.FolderName != null ? "Missing" : "Not Installed";
            Detail = Entry.FolderName != null ? "This tracked addon has no .toc folder on disk. Its source association is preserved." : "Select Install to download this repository.";
            return;
        }
        if (Model == null) { Status = "Installed - Unmanaged"; Detail = "Assign a GitHub source to manage updates. The existing files will be preserved."; return; }
        if (Model.IsCustom) { Status = "Installed - Custom Sync"; Detail = "Custom addon files received through rclone. Use Sync from Profile to check for changes; local edits are protected."; return; }
        if (Latest == null)
        {
            Status = Model.AdoptedFolders.Any(f => f.Name.Equals(Folder, StringComparison.OrdinalIgnoreCase)) ? "Installed - Version Unknown" : "Installed";
            Detail = "Source assigned. Check the repository for its latest version.";
            return;
        }
        var comparison = AddonVersionComparer.Compare(Model, Entry.FolderName, Entry.Local?.Metadata.Version, Latest);
        Status = comparison switch { VersionComparison.Current => "Current", VersionComparison.Different => "Update Available", _ => "Installed - Version Unknown" };
        Detail = comparison switch
        {
            VersionComparison.Current => "The installed version matches the latest GitHub version.",
            VersionComparison.Different => "The installed version differs from the latest GitHub version. Update applies to the associated source group.",
            _ => "The local version cannot be reliably compared with GitHub. Update Selected is available if you choose to replace it."
        };
    }
}
