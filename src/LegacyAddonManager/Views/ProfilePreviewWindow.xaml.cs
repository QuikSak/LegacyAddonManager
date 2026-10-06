using System.Windows;
using LegacyAddonManager.Core;

namespace LegacyAddonManager.Views;

public sealed record ProfilePreviewLine(string Title, string Action, string Detail);

public partial class ProfilePreviewWindow : Window
{
    private readonly int changes;
    public bool RemoveExtras => RemoveExtrasBox.IsChecked == true;

    public ProfilePreviewWindow(ProfileExport export, int unmanagedCount)
    {
        InitializeComponent();
        Title = "Publish addon profile";
        Heading.Text = "Publish this computer's addons";
        SummaryText.Text = $"{export.Profile.Addons.Count(a => !a.IsCustom)} GitHub groups · {export.Profile.Addons.Count(a => a.IsCustom)} custom groups with files to upload. Followers can preview and apply this profile.";
        ChangesList.ItemsSource = export.Profile.Addons.Select(a => new ProfilePreviewLine(a.DisplayName, a.IsCustom ? "Upload custom files" : "Publish " + a.Version,
                $"Folders: {string.Join(", ", a.Folders)}\nPackage: {(a.IsCustom ? "Installed files ZIP · " + a.ContentHash[..12] : a.AssetName ?? "Source archive")}"))
            .Concat(export.Excluded.Select(a => new ProfilePreviewLine(a, "Excluded", "Install/update through the manager, or select installed files in publish options.")))
            .Concat(new[] { new ProfilePreviewLine($"{unmanagedCount} unmanaged addons", "Excluded", "Local-only addons stay on this computer.") }).ToArray();
        SafetyText.Text = "Selected custom addon folders are uploaded with their contents. WoW paths, app credentials, ownership markers and WTF settings stay local. Publishing replaces the previous profile. Both computers need version 1.4 or newer for custom files.";
        ApplyButton.Content = "Publish Profile";
        changes = Math.Max(1, export.Profile.Addons.Count);
    }

    public ProfilePreviewWindow(SyncPlan plan, SyncProfile profile)
    {
        InitializeComponent();
        Title = "Sync addon profile";
        Heading.Text = "Preview changes on this computer";
        SummaryText.Text = $"Published {profile.PublishedAt.ToLocalTime():g} · {plan.Changes} source groups to install or replace · {plan.Items.Count - plan.Changes} current · {plan.UnmanagedCount} unmanaged addons preserved.";
        ChangesList.ItemsSource = plan.Items.Select(i => new ProfilePreviewLine(i.Target.DisplayName, i.Action, i.Detail))
            .Concat(plan.Extras.Select(a => new ProfilePreviewLine(a.IsCustom ? a.Name + " (custom files)" : a.RepositoryUrl, "Extra — keep by default", $"Folders: {string.Join(", ", a.InstalledFolders)}\nOnly removed if the option below is checked."))).ToArray();
        RemoveExtrasBox.Visibility = plan.Extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SafetyText.Text = "Close WoW first. Replacements may downgrade versions and replace adopted files. Changes finish one source at a time; completed changes remain if a later source fails or you cancel. Unmanaged addons and WTF settings are preserved.";
        changes = plan.Changes;
        UpdateApplyButton();
    }

    private void RemovalChanged(object sender, RoutedEventArgs e) => UpdateApplyButton();
    private void UpdateApplyButton() { if (ApplyButton != null) ApplyButton.IsEnabled = changes > 0 || RemoveExtras; }
    private void Apply_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
}
