using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using LegacyAddonManager.Core;
using LegacyAddonManager.Services;

namespace LegacyAddonManager.Views;

public partial class RcloneSetupWindow : Window
{
    private readonly BundledRclone bundled;
    private readonly string localDirectory;
    private readonly CancellationTokenSource cancellation = new();
    private string protectedPassword;
    private bool busy;
    public RcloneConnectionResult? Result { get; private set; }

    public RcloneSetupWindow(AppSettings settings, string localDirectory)
    {
        this.localDirectory = localDirectory;
        bundled = new(localDirectory);
        protectedPassword = settings.ProtectedRclonePassword;
        InitializeComponent();
        ConfigBox.Text = settings.RcloneConfigPath.Length > 0 ? settings.RcloneConfigPath : Path.Combine(localDirectory, "rclone.conf");
        RemotePathBox.Text = settings.RcloneRemoteProfile;
        PasswordOptions.IsExpanded = protectedPassword.Length > 0;
        StatusText.Text = $"rclone {BundledRclone.Version} is included. Configure a remote or select your existing config, then test the connection.";
        Closed += (_, _) => cancellation.Cancel();
    }
    private string Password => ClearPasswordBox.IsChecked == true ? "" : PasswordInput.Password.Length > 0
        ? PasswordInput.Password : TokenProtector.Unprotect(protectedPassword);
    private string PasswordStorage => Password.Length == 0 ? "" : TokenProtector.ProtectExact(Password);
    private async Task<RcloneProcessRunner> RunnerAsync() => new(await bundled.EnsureAvailableAsync(cancellation.Token), ConfigBox.Text, Password);

    private void ConfigurationChanged(object sender, RoutedEventArgs e) { if (SaveButton != null) SaveButton.IsEnabled = false; }
    private void RemoteChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RemoteBox.SelectedItem is not string remote) return;
        var suffix = RemotePathBox.Text.Contains(':') ? RemotePathBox.Text[(RemotePathBox.Text.IndexOf(':') + 1)..] : "WoW/legacy-addons.json";
        RemotePathBox.Text = remote + suffix;
    }
    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose an existing rclone config", Filter = "rclone config (*.conf)|*.conf|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        ConfigBox.Text = dialog.FileName;
        protectedPassword = "";
        PasswordInput.Clear();
        RemoteBox.ItemsSource = null;
    }
    private async void Configure_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var executable = await bundled.EnsureAvailableAsync(cancellation.Token);
        var config = ConfigBox.Text;
        if (!Path.IsPathFullyQualified(config)) throw new IOException("Choose an absolute local config path.");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        // This console is explicitly requested by the Configure Remote button.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        if (Password.Length > 0) start.Environment["RCLONE_CONFIG_PASS"] = Password;
        foreach (var arg in new[] { "config", "--config", config }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not open the rclone configuration wizard.");
        StatusText.Text = "Complete account setup in the rclone console, then close it to return here.";
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await RefreshRemotesAsync();
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshRemotesAsync);
    private async Task RefreshRemotesAsync()
    {
        var remotes = await RcloneProfileTransport.ListRemotesAsync(await RunnerAsync(), cancellation.Token);
        RemoteBox.ItemsSource = remotes;
        var selected = remotes.FirstOrDefault(r => RemotePathBox.Text.StartsWith(r, StringComparison.Ordinal));
        RemoteBox.SelectedItem = selected ?? remotes.FirstOrDefault();
        StatusText.Text = remotes.Count == 0 ? "No supported remotes found. Configure a remote with a name such as cloud, then refresh." : $"Found {remotes.Count} remotes. Choose a profile filename and test the connection.";
    }
    private async void Test_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var remote = RemotePathBox.Text.Trim();
        RcloneProfileTransport.ValidateRemotePath(remote);
        var transport = new RcloneProfileTransport(await RunnerAsync(), ConfigBox.Text, remote, Path.Combine(localDirectory, "SyncProfiles"));
        var profile = await transport.ReadAsync(true, cancellation.Token);
        StatusText.Text = profile == null ? "Connected. This filename has no profile yet; publishing will create it. Upload permissions are checked when you publish."
            : $"Connected. Found {profile.Profile.Addons.Count} source groups, published {profile.Profile.PublishedAt.ToLocalTime():g}.";
        SaveButton.IsEnabled = true;
    });
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; SetInputs(false); SaveButton.IsEnabled = false;
        StatusText.Text = "Working…";
        try { await action(); }
        catch (OperationCanceledException) { StatusText.Text = "Setup cancelled."; }
        catch (Exception ex)
        { StatusText.Text = ex is CryptographicException or FormatException ? "The saved config password could not be decrypted. Enter it again or clear it." : ex.Message; }
        finally { busy = false; SetInputs(true); }
    }
    private void SetInputs(bool enabled)
    {
        ConfigBox.IsEnabled = enabled; PasswordInput.IsEnabled = enabled; ClearPasswordBox.IsEnabled = enabled;
        RemoteBox.IsEnabled = enabled; RemotePathBox.IsEnabled = enabled; ConfigureButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled; TestButton.IsEnabled = enabled;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !SaveButton.IsEnabled) return;
        Result = new(ConfigBox.Text, RemotePathBox.Text.Trim(), PasswordStorage);
        PasswordInput.Clear(); DialogResult = true;
    }
}
