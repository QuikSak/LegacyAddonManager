using System.IO;
using System.Windows;
using LegacyAddonManager.Core;
using LegacyAddonManager.ViewModels;

namespace LegacyAddonManager;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, "Local\\LegacyAddonManager-" + Environment.UserName, out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("Legacy Addon Manager is already running.", "Legacy Addon Manager");
            Shutdown();
            return;
        }
        try
        {
            var store = new SettingsStore();
            var vm = new MainViewModel(store.Load(), store);
            MainWindow = new MainWindow { DataContext = vm };
            MainWindow.ContentRendered += OnMainWindowContentRendered;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to start Legacy Addon Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private async void OnMainWindowContentRendered(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.ContentRendered -= OnMainWindowContentRendered;
        if (window.DataContext is MainViewModel vm) await vm.CheckUpdatesOnStartupAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel vm) vm.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
