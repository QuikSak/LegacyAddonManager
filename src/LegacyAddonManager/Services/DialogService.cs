using System.Windows;
using LegacyAddonManager.Core;
using LegacyAddonManager.Views;

namespace LegacyAddonManager.Services;

public interface IUserDialogs
{
    string? RepositoryUrl();
    string? EditSource(string name, string source);
    string? BrowseWoW(string current);
    TokenSettingsResult? Settings(bool hasToken);
    DownloadOption? ChooseDownload(RepositoryVersion version);
    bool ConfirmRemove(ManagedAddon addon);
    bool ConfirmStopManaging(string folder);
    bool ConfirmFirstUpdate(ManagedAddon addon);
    bool ConfirmPublish(ProfileExport export, int unmanagedCount);
    IReadOnlyList<string>? ChoosePublishFiles(IReadOnlyList<LocalPublishCandidate> candidates, IReadOnlyCollection<string> previous);
    ProfileSyncChoice? PreviewSync(SyncPlan plan, SyncProfile profile);
    RcloneConnectionResult? ConfigureRclone(AppSettings settings, string localDirectory);
    void Error(string message);
}

public sealed class DialogService : IUserDialogs
{
    private static Window Owner => Application.Current.MainWindow;
    public string? RepositoryUrl()
    {
        var dialog = new RepositoryWindow { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.RepositoryUrl : null;
    }
    public string? EditSource(string name, string source)
    {
        var dialog = new RepositoryWindow(name, source) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.RepositoryUrl : null;
    }
    public string? BrowseWoW(string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select your WoW 3.3.5a folder (contains Wow.exe)", Multiselect = false };
        if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }
    public TokenSettingsResult? Settings(bool hasToken)
    {
        var dialog = new SettingsWindow(hasToken) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
    public DownloadOption? ChooseDownload(RepositoryVersion version)
    {
        var dialog = new DownloadWindow(version) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.SelectedDownload : null;
    }
    public bool ConfirmRemove(ManagedAddon addon) => MessageBox.Show(Owner,
        addon.InstalledFolders.Count > 0
            ? $"Uninstall ALL folders associated with {(addon.IsCustom ? addon.Name + " (custom sync)" : addon.RepositoryUrl)}?\n\n{string.Join("\n", addon.InstalledFolders)}\n\nThese addon files will be removed, including adopted folders. Other addons and WoW saved variables are preserved. To keep these files, use Stop Managing instead."
            : $"Remove {addon.Name} from the managed list?",
        "Remove Addon — uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public bool ConfirmStopManaging(string folder) => MessageBox.Show(Owner,
        $"Remove the source association for {folder}?\n\nAll installed files will be preserved. Other folders sharing this repository remain managed.",
        "Stop Managing — keep files", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    public bool ConfirmFirstUpdate(ManagedAddon addon) => MessageBox.Show(Owner,
        $"Replace the adopted files in this source group with the selected GitHub package?\n\n{string.Join("\n", addon.InstalledFolders)}\n\nThis is the first manager update for: {string.Join(", ", addon.AdoptedFolders.Select(f => f.Name))}.\n\nLocal versions may be unknown or newer than the release. Associated folders absent from the package will be removed. The operation validates the adoption snapshot, stages replacements, and rolls back on failure.",
        "Update adopted addon", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void Error(string message) => MessageBox.Show(Owner, message, "Legacy Addon Manager", MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool ConfirmPublish(ProfileExport export, int unmanagedCount) =>
        new ProfilePreviewWindow(export, unmanagedCount) { Owner = Owner }.ShowDialog() == true;
    public IReadOnlyList<string>? ChoosePublishFiles(IReadOnlyList<LocalPublishCandidate> candidates, IReadOnlyCollection<string> previous)
    {
        var dialog = new PublishOptionsWindow(candidates, previous) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.SelectedFolders : null;
    }
    public ProfileSyncChoice? PreviewSync(SyncPlan plan, SyncProfile profile)
    {
        var dialog = new ProfilePreviewWindow(plan, profile) { Owner = Owner };
        return dialog.ShowDialog() == true ? new(dialog.RemoveExtras) : null;
    }
    public RcloneConnectionResult? ConfigureRclone(AppSettings settings, string localDirectory)
    {
        var dialog = new RcloneSetupWindow(settings, localDirectory) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}

public sealed record TokenSettingsResult(string Token, bool ClearToken);
public sealed record ProfileSyncChoice(bool RemoveExtras);
public sealed record RcloneConnectionResult(string ConfigPath, string RemoteProfile, string ProtectedPassword);
