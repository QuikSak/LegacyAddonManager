using System.Windows;
using LegacyAddonManager.Core;
using LegacyAddonManager.ViewModels;

namespace LegacyAddonManager.Views;

public sealed class PublishCandidateViewModel(LocalPublishCandidate candidate, bool selected) : ObservableObject
{
    public LocalPublishCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public string Folders => "Folders: " + string.Join(", ", Candidate.Folders);
    public string Description => Candidate.Description;
    private bool isSelected = selected;
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
}

public partial class PublishOptionsWindow : Window
{
    public IReadOnlyList<PublishCandidateViewModel> Candidates { get; }
    public IReadOnlyList<string> SelectedFolders => IncludeCustomBox.IsChecked == true
        ? Candidates.Where(c => c.IsSelected).SelectMany(c => c.Candidate.Folders).ToArray() : [];
    public PublishOptionsWindow(IReadOnlyList<LocalPublishCandidate> candidates, IReadOnlyCollection<string> previous)
    {
        InitializeComponent();
        Candidates = candidates.Select(c => new PublishCandidateViewModel(c, c.Folders.Any(f => previous.Contains(f, StringComparer.OrdinalIgnoreCase)))).ToArray();
        CandidatesList.ItemsSource = Candidates;
        IncludeCustomBox.IsChecked = Candidates.Any(c => c.IsSelected);
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var candidate in Candidates) candidate.IsSelected = true; }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var candidate in Candidates) candidate.IsSelected = false; }
    private void Review_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
}
