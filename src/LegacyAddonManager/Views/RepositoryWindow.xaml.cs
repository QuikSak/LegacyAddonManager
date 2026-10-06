using System.Windows;
using LegacyAddonManager.Core;

namespace LegacyAddonManager.Views;

public partial class RepositoryWindow : Window
{
    public string RepositoryUrl { get; private set; } = "";
    private bool allowEmpty;
    public RepositoryWindow() { InitializeComponent(); Loaded += (_, _) => UrlBox.Focus(); }
    public RepositoryWindow(string name, string source) : this()
    {
        allowEmpty = true;
        Title = "Edit Source";
        Heading.Text = "Source for " + name;
        Description.Text = "Assign a GitHub repository without changing installed files. Folders using the same source share update operations. Leave blank to stop managing this folder.";
        UrlBox.Text = source;
        SaveButton.Content = "Save Source";
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try { RepositoryUrl = allowEmpty && string.IsNullOrWhiteSpace(UrlBox.Text) ? "" : GitHubRepository.Parse(UrlBox.Text).Url; DialogResult = true; }
        catch (ArgumentException ex) { ValidationText.Text = ex.Message; }
    }
}
