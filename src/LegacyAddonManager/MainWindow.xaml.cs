using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LegacyAddonManager.ViewModels;

namespace LegacyAddonManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
    }
    private void AddonRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row) SelectContextRow(row);
    }
    private void AddonRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is DataGridRow row) SelectContextRow(row);
    }
    private static void SelectContextRow(DataGridRow row)
    {
        row.SetCurrentValue(DataGridRow.IsSelectedProperty, true);
        row.Focus();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel { IsBusy: true } vm)
        {
            e.Cancel = true;
            vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
