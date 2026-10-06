using System.Windows;
using LegacyAddonManager.Services;

namespace LegacyAddonManager.Views;

public partial class SettingsWindow : Window
{
    public TokenSettingsResult? Result { get; private set; }
    public SettingsWindow(bool hasToken)
    {
        InitializeComponent();
        TokenState.Text = hasToken ? "A token is saved for this Windows user." : "Using public, unauthenticated access.";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new(TokenBox.Password, ClearBox.IsChecked == true);
        TokenBox.Clear();
        DialogResult = true;
    }
}
