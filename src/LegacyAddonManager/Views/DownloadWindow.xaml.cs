using System.Windows;
using LegacyAddonManager.Core;

namespace LegacyAddonManager.Views;

public partial class DownloadWindow : Window
{
    public DownloadOption? SelectedDownload => DownloadsList.SelectedItem as DownloadOption;
    public DownloadWindow(RepositoryVersion version)
    {
        InitializeComponent();
        DownloadsList.ItemsSource = version.Downloads;
        DownloadsList.SelectedIndex = 0;
    }
    private void Choose_Click(object sender, RoutedEventArgs e) { if (SelectedDownload != null) DialogResult = true; }
}
