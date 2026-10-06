using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LegacyAddonManager;
using LegacyAddonManager.Core;
using LegacyAddonManager.Services;
using LegacyAddonManager.ViewModels;
using LegacyAddonManager.Views;

internal static class UiSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), ".lam-ui-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Use the real theme with an isolated Application, bypassing production startup.
            var app = new Application
            {
                Resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/LegacyAddonManager;component/Themes/Dark.xaml") }
            };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var wow = MakeWoW(Path.Combine(root, "WoW"));
            var addOns = Path.Combine(wow, "Interface", "AddOns");
            WriteAddon(addOns, "RefactorGear", "Refactor Gear", "1.0.0");
            WriteAddon(addOns, "DBM-Core", "Deadly Boss Mods: Core", "1.2.3");
            WriteAddon(addOns, "DBM-GUI", "Deadly Boss Mods: GUI", "1.2.3");
            WriteAddon(addOns, "QuestHelper", "Quest Helper", "0.9.5");
            WriteAddon(addOns, "LegacyNotes", "Legacy Notes", "@project-version@");
            var store = new SettingsStore(Path.Combine(root, "settings"));
            var settings = new AppSettings { WoWPath = wow, Addons =
            [
                new() { Name = "RefactorGear", RepositoryUrl = "https://github.com/ProfetGit/RefactorGear", InstalledVersion = "v1.0.0",
                    InstalledVersionKind = "release", InstallationId = "fixture-id", InstalledFolders = ["RefactorGear"], AddOnsPath = addOns },
                new() { Name = "OldTools", RepositoryUrl = "https://github.com/example/OldTools", InstalledVersion = "v0.8",
                    InstalledVersionKind = "release", InstallationId = "missing-fixture", InstalledFolders = ["OldTools"], AddOnsPath = addOns }
            ] };
            var management = new AddonManagement(store);
            management.AssignSource(settings, "DBM-Core", "https://github.com/example/DBM");
            management.AssignSource(settings, "DBM-GUI", "https://github.com/example/DBM");
            management.AssignSource(settings, "LegacyNotes", "https://github.com/example/LegacyNotes");
            using var vm = new MainViewModel(settings, store);
            foreach (var row in vm.Addons)
            {
                if (row.Folder == "RefactorGear") row.Latest = new("v1.1.0", "release", []);
                if (row.Folder.StartsWith("DBM-")) row.Latest = new("v1.2.3", "release", []);
                if (row.Folder == "LegacyNotes") row.Latest = new("v2.0.0", "release", []);
                row.RefreshStatus();
            }
            vm.Selected = vm.Addons.Single(a => a.Folder == "RefactorGear");
            Assert(vm.Addons.Single(a => a.Folder == "QuestHelper").Status == "Installed - Unmanaged", "Startup must discover unmanaged folders.");
            Assert(vm.Addons.Single(a => a.Folder == "OldTools").Status == "Missing", "Missing records must remain visible.");
            var main = new MainWindow { DataContext = vm };
            Assert(main.Background is SolidColorBrush { Color: var color } && color == Color.FromRgb(16, 20, 28), "Window must use the dark theme.");
            app.MainWindow = main;
            var output = args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots");
            Directory.CreateDirectory(output);
            Render(main, Path.Combine(output, "main.png"));
            Assert(!Descendants<Button>(main).Any(b => Equals(b.Content, "Choose Sync Profile…")), "The shared-file selector must be removed.");
            Assert(!vm.PublishProfileCommand.CanExecute(null) && !vm.SyncProfileCommand.CanExecute(null), "Sync actions require a configured rclone connection.");
            VerifyRowMenu(main, vm, Path.Combine(output, "addon-context-menu.png"));
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            Render(main, Path.Combine(output, "main-minimum.png"));
            var repository = new RepositoryWindow();
            Render(repository, Path.Combine(output, "repository.png"));
            ((TextBox)repository.FindName("UrlBox")).Text = "https://evil.example/owner/repo";
            Descendants<Button>(repository).Single(b => Equals(b.Content, "Add Repository")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(((TextBlock)repository.FindName("ValidationText")).Text.Length > 0, "Invalid repository URL must show validation.");
            var source = new RepositoryWindow("Quest Helper", "https://github.com/example/QuestHelper");
            Render(source, Path.Combine(output, "edit-source.png"));
            var settingsWindow = new SettingsWindow(true);
            Render(settingsWindow, Path.Combine(output, "settings.png"));
            var downloads = new DownloadWindow(new("v2", "release", [new("Addon-Wrath.zip", "", false), new("Addon-Retail.zip", "", false), new("Source archive", "", true)]));
            Render(downloads, Path.Combine(output, "downloads.png"));
            Assert(downloads.SelectedDownload?.Name == "Addon-Wrath.zip", "Release package selection must be populated.");
            var encrypted = TokenProtector.Protect("test-only-token");
            Assert(encrypted != "test-only-token" && TokenProtector.Unprotect(encrypted) == "test-only-token", "DPAPI roundtrip failed.");
            Assert(vm.CheckCommand.CanExecute(null) && vm.UpdateSelectedCommand.CanExecute(null), "Main window commands must be available.");
            VerifyWorkflow(Path.Combine(root, "workflow"));
            VerifyStartupChecks(Path.Combine(root, "startup-checks"));
            VerifyRcloneWorkflow(Path.Combine(root, "rclone-workflow"), output);
            VerifyCustomRcloneWorkflow(Path.Combine(root, "custom-rclone-workflow"), output);
            main.Close(); repository.Close(); source.Close(); settingsWindow.Close(); downloads.Close();
            Console.WriteLine("PASS  WPF main window and dialogs render; URL validation, command availability, and DPAPI work.");
            Console.WriteLine($"Screenshots: {Path.GetFullPath(output)}");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) SafeFiles.DeleteOwnedWorkDirectory(root, Path.GetTempPath()); }
    }

    private static void VerifyRowMenu(MainWindow window, MainViewModel vm, string screenshot)
    {
        var grid = Descendants<DataGrid>(window).Single();
        foreach (var folder in new[] { "QuestHelper", "RefactorGear" })
        {
            var addon = vm.Addons.Single(a => a.Folder == folder);
            grid.ScrollIntoView(addon);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            grid.UpdateLayout();
            var row = grid.ItemContainerGenerator.ContainerFromItem(addon) as DataGridRow
                ?? throw new Exception("The addon row was not generated.");
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            Assert(ReferenceEquals(vm.Selected, addon), "Right-click must select the clicked addon instead of acting on the previously selected one.");
            var menu = row.ContextMenu ?? throw new Exception("The addon has no context menu.");
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            try
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Assert(ReferenceEquals(menu.DataContext, vm) && items.All(i => i.Command != null), "Popup commands must bind to the main view model.");
                var edit = items.Single(i => Equals(i.Header, "Edit Source"));
                var remove = items.Single(i => Equals(i.Header, "Remove Addon…"));
                Assert(ReferenceEquals(edit.Command, vm.EditSourceCommand) && edit.IsEnabled, "The menu must use the existing Edit Source function.");
                Assert(ReferenceEquals(remove.Command, vm.RemoveCommand) && remove.IsEnabled == addon.IsManaged,
                    "Unmanaged addons must have Remove Addon disabled; managed rows must follow the button's rules.");
                if (addon.IsManaged)
                {
                    menu.UpdateLayout();
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen())
                        context.DrawRectangle(new VisualBrush(menu), null, new Rect(0, 0, menu.ActualWidth, menu.ActualHeight));
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(drawing);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(screenshot); encoder.Save(stream);
                }
            }
            finally { menu.IsOpen = false; }
        }
        Console.WriteLine("PASS  Right-click selects the target row; popup actions reuse button commands and preserve unmanaged removal protection.");
    }

    private static void VerifyWorkflow(string root)
    {
        var wow = MakeWoW(Path.Combine(root, "WoW"));
        var addOns = Path.Combine(wow, "Interface", "AddOns");
        WriteAddon(addOns, "ManualAddon", "A Manual Addon", "1.2.3");
        WriteAddon(addOns, "Companion", "Companion", "1.2.3");
        WriteAddon(addOns, "Mystery", "Mystery", "r123");
        var settings = new AppSettings { WoWPath = wow };
        var store = new SettingsStore(Path.Combine(root, "settings"));
        var dialogs = new FakeDialogs();
        using var handler = new FakeGitHub();
        using var vm = new MainViewModel(settings, store, dialogs, new GitHubService(handler));
        Assert(vm.Addons.Count == 3 && vm.Addons.All(a => a.Status == "Installed - Unmanaged"), "Live disk scan must populate an empty database.");
        Assert(!File.Exists(store.FilePath), "Discovering unmanaged addons must not create tracking records.");
        var before = SafeFiles.HashFiles(addOns, "");
        vm.Selected = vm.Addons.Single(a => a.Folder == "ManualAddon");
        Assert(!vm.RemoveCommand.CanExecute(null), "Unmanaged folders must not be uninstallable through managed removal.");
        dialogs.Source = "https://github.com/owner/Example.git/";
        Execute(vm, vm.EditSourceCommand);
        Assert(vm.Selected!.Source == "https://github.com/owner/Example" && vm.Selected.Status == "Current", "Source assignment must normalize and compare the TOC version.");
        EqualTree(before, SafeFiles.HashFiles(addOns, ""));
        Assert(store.Load().Addons.Single().InstalledVersion == null, "Adoption must not invent a GitHub installation identity.");
        vm.Selected = vm.Addons.Single(a => a.Folder == "Companion");
        Execute(vm, vm.EditSourceCommand);
        Assert(settings.Addons.Count == 1 && settings.Addons.Single().InstalledFolders.Count == 2, "Equal sources must share a repository group.");
        vm.Selected = vm.Addons.Single(a => a.Folder == "Mystery");
        dialogs.Source = "https://github.com/owner/Mystery";
        Execute(vm, vm.EditSourceCommand);
        Assert(vm.Selected!.Status == "Installed - Version Unknown", "An inconclusive local version must not be declared outdated.");
        var checks = handler.Checks;
        Execute(vm, vm.CheckCommand);
        Assert(handler.Checks - checks == 2, "Check All must query a shared repository once.");
        Execute(vm, vm.UpdateAllCommand);
        Assert(handler.Downloads == 0 && dialogs.UpdateConfirmations == 0, "Update All must preserve unknown-only groups.");
        vm.Selected = vm.Addons.Single(a => a.Folder == "Mystery");
        Execute(vm, vm.UpdateSelectedCommand);
        Assert(dialogs.UpdateConfirmations == 1 && handler.Downloads == 0, "An adopted first update must require the in-app confirmation.");
        EqualTree(before, SafeFiles.HashFiles(addOns, ""));
        handler.Fail = true;
        vm.Selected = vm.Addons.Single(a => a.Folder == "ManualAddon");
        Execute(vm, vm.CheckSelectedCommand);
        Assert(vm.Selected!.Status == "Error" && vm.Selected.Source.Length > 0, "Network errors must preserve source associations.");
        EqualTree(before, SafeFiles.HashFiles(addOns, ""));
        handler.Fail = false;
        Execute(vm, vm.StopManagingCommand);
        Assert(vm.Selected!.Status == "Installed - Unmanaged" && vm.Selected.Source == "", "Stop Managing must return the folder to unmanaged state.");
        Assert(settings.Addons.Single(a => a.RepositoryUrl.EndsWith("/Example")).InstalledFolders.SequenceEqual(["Companion"]), "Stopping one folder must keep siblings managed.");
        EqualTree(before, SafeFiles.HashFiles(addOns, ""));
        using (var restarted = new MainViewModel(store.Load(), store, dialogs, new GitHubService(new FakeGitHub())))
            Assert(restarted.Addons.Count == 3 && restarted.Addons.Single(a => a.Folder == "ManualAddon").Source == "" && restarted.Addons.Single(a => a.Folder == "Companion").IsManaged, "Associations must survive restart.");
        WriteAddon(addOns, "NewOnDisk", "New On Disk", "1.0");
        Execute(vm, vm.RefreshCommand);
        Assert(vm.Addons.Any(a => a.Folder == "NewOnDisk" && !a.IsManaged), "Refresh must discover newly installed manual folders.");
        Directory.Move(Path.Combine(addOns, "Mystery"), Path.Combine(root, "Mystery-backup"));
        Execute(vm, vm.RefreshCommand);
        Assert(vm.Addons.Single(a => a.Folder == "Mystery").Status == "Missing", "Refresh must retain missing managed records.");
        var second = MakeWoW(Path.Combine(root, "OtherWoW"));
        WriteAddon(Path.Combine(second, "Interface", "AddOns"), "OtherAddon", "Other Addon", "1.0");
        dialogs.WoWPath = second;
        Execute(vm, vm.BrowseCommand);
        Assert(vm.Addons.Count == 1 && vm.Addons.Single().Folder == "OtherAddon", "Changing WoW path must rescan the selected installation.");
        dialogs.WoWPath = wow;
        Execute(vm, vm.BrowseCommand);
        Assert(vm.Addons.Any(a => a.Folder == "Companion" && a.IsManaged) && vm.Addons.Any(a => a.Folder == "Mystery" && a.Status == "Missing"), "Switching back must restore source associations.");
        vm.Selected = vm.Addons.Single(a => a.Folder == "Companion");
        Execute(vm, vm.CheckSelectedCommand);
        Assert(vm.Selected!.Status == "Current", "The adopted fixture must already match the release.");
        var beforeFirstInstall = SafeFiles.HashFiles(addOns, "");
        var confirmations = dialogs.UpdateConfirmations;
        Execute(vm, vm.UpdateSelectedCommand);
        Assert(dialogs.UpdateConfirmations == confirmations + 1 && handler.Downloads == 0, "A current adopted source must allow a confirmed first manager installation for publication.");
        EqualTree(beforeFirstInstall, SafeFiles.HashFiles(addOns, ""));
        Assert(dialogs.Errors.Count == 0, string.Join("\n", dialogs.Errors));
        Console.WriteLine("PASS  MVVM workflow: startup discovery, adoption without writes, Current/Unknown comparison, grouped checks, safe stop-managing, refresh, Missing, restart, and path changes.");
    }

    private static void VerifyStartupChecks(string root)
    {
        var wow = MakeWoW(Path.Combine(root, "WoW"));
        var addOns = Path.Combine(wow, "Interface", "AddOns");
        foreach (var folder in new[] { "Updated", "Current", "Companion", "Custom", "Unmanaged" })
            WriteAddon(addOns, folder, folder, "1.2.3");
        var settings = new AppSettings { WoWPath = wow, Addons =
        [
            new() { Name = "Updated", RepositoryUrl = "https://github.com/example/Updated", InstalledVersion = "v1.0.0",
                InstalledVersionKind = "release", InstallationId = "updated-fixture", InstalledFolders = ["Updated"], AddOnsPath = addOns },
            new() { Name = "Current", RepositoryUrl = "https://github.com/example/Current", InstalledVersion = "v1.2.3",
                InstalledVersionKind = "release", InstallationId = "current-fixture", InstalledFolders = ["Current", "Companion"], AddOnsPath = addOns },
            new() { Name = "Custom", CustomPublisherId = Guid.NewGuid(), CustomSourceId = new string('A', 64), InstalledVersion = new string('B', 64),
                InstalledVersionKind = "custom", InstallationId = "custom-fixture", InstalledFolders = ["Custom"], AddOnsPath = addOns },
            new() { Name = "Other installation", RepositoryUrl = "https://github.com/example/Other",
                AddOnsPath = Path.Combine(root, "OtherWoW", "Interface", "AddOns") }
        ] };
        var store = new SettingsStore(Path.Combine(root, "settings"));
        store.Save(settings);
        var savedSettings = File.ReadAllText(store.FilePath);
        var before = SafeFiles.HashFiles(addOns, "");
        var dialogs = new FakeDialogs();
        using (var handler = new FakeGitHub())
        using (var vm = new MainViewModel(store.Load(), store, dialogs, new GitHubService(handler)))
        {
            Assert(handler.Checks == 0, "Constructing the view model must not start networking before the window renders.");
            var check = vm.CheckUpdatesOnStartupAsync();
            Assert(vm.IsBusy && ReferenceEquals(check, vm.CheckUpdatesOnStartupAsync()), "Startup must share a single in-flight check.");
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(!vm.IsBusy && handler.Checks == 2 && handler.Downloads == 0, "Startup must check each active GitHub source group once without downloads.");
            Assert(vm.Addons.Single(a => a.Folder == "Updated").Status == "Update Available", "Startup must compare the tracked installed version.");
            Assert(vm.Addons.Where(a => a.Folder is "Current" or "Companion").All(a => a.Status == "Current"), "All folders in the current source group must be refreshed.");
            Assert(vm.Addons.Single(a => a.Folder == "Custom").Status == "Installed - Custom Sync" &&
                vm.Addons.Single(a => a.Folder == "Unmanaged").Status == "Installed - Unmanaged", "Startup must preserve custom and unmanaged statuses.");
            vm.CheckUpdatesOnStartupAsync().GetAwaiter().GetResult();
            Assert(handler.Checks == 2, "Repeated window render events must not repeat the startup check.");
        }
        using (var handler = new FakeGitHub())
        using (var vm = new MainViewModel(store.Load(), store, dialogs, new GitHubService(handler)))
        {
            var check = vm.CheckUpdatesOnStartupAsync();
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(handler.Checks == 2, "A fresh app launch must check again regardless of the previous launch.");
        }
        using (var handler = new FakeGitHub { Fail = true })
        using (var vm = new MainViewModel(store.Load(), store, dialogs, new GitHubService(handler)))
        {
            var check = vm.CheckUpdatesOnStartupAsync();
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(!vm.IsBusy && vm.Addons.Count(a => a.Status == "Error") == 3 && dialogs.Errors.Count == 0, "Startup network failures must appear in rows without blocking the window with dialogs.");
            handler.Fail = false;
            Execute(vm, vm.CheckCommand);
            Assert(vm.Addons.All(a => a.Status != "Error"), "Manual checks must recover after a startup network failure.");
        }
        using (var handler = new FakeGitHub { RateLimited = true })
        using (var vm = new MainViewModel(store.Load(), store, dialogs, new GitHubService(handler)))
        {
            var check = vm.CheckUpdatesOnStartupAsync();
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(!vm.IsBusy && handler.Checks == 1 && vm.Message.Contains("rate limit") && dialogs.Errors.Count == 0,
                "Startup must stop on a GitHub rate limit and show the retry guidance without a modal dialog.");
        }
        using (var handler = new FakeGitHub { WaitForCancellation = true })
        using (var vm = new MainViewModel(store.Load(), store, dialogs, new GitHubService(handler)))
        {
            var check = vm.CheckUpdatesOnStartupAsync();
            PumpUntil(() => handler.Checks == 1);
            vm.CancelCommand.Execute(null);
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(!vm.IsBusy && vm.Message == "Operation cancelled." && vm.Addons.All(a => a.Status != "Checking") && handler.Checks == 1,
                "Startup checks must remain cancellable and stop before querying another source.");
        }
        var skipCases = new[]
        {
            new AppSettings { Addons = [new() { RepositoryUrl = "https://github.com/example/Pending" }] },
            new AppSettings { WoWPath = wow, Addons = settings.Addons.Where(a => a.IsCustom || a.Name == "Other installation").ToList() },
            new AppSettings { WoWPath = Path.Combine(root, "MissingWoW"), Addons = [new() { RepositoryUrl = "https://github.com/example/Pending" }] }
        };
        foreach (var skipped in skipCases)
        {
            using var handler = new FakeGitHub();
            using var vm = new MainViewModel(skipped, store, dialogs, new GitHubService(handler));
            var check = vm.CheckUpdatesOnStartupAsync();
            PumpUntil(() => check.IsCompleted); check.GetAwaiter().GetResult();
            Assert(handler.Checks == 0 && !vm.IsBusy && dialogs.Errors.Count == 0, "Unconfigured, custom-only and invalid-path launches must avoid GitHub traffic and modal errors.");
        }
        EqualTree(before, SafeFiles.HashFiles(addOns, ""));
        Assert(File.ReadAllText(store.FilePath) == savedSettings, "Startup checks must leave saved installation versions and settings unchanged.");
        Console.WriteLine("PASS  Automatic launch checks: once per launch, grouped Current/Update Available, custom/unmanaged/path exclusions, no downloads or file changes, network recovery, rate limits, and cancellation.");
    }

    private static void Execute(MainViewModel vm, RelayCommand command)
    {
        Assert(command.CanExecute(null), "The requested UI action is disabled.");
        command.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsBusy)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI action did not finish.");
            Thread.Sleep(5);
        }
    }

    private static void VerifyRcloneWorkflow(string root, string output)
    {
        var remoteRoot = Path.Combine(root, "remote storage"); Directory.CreateDirectory(remoteRoot);
        var publisherStore = new SettingsStore(Path.Combine(root, "publisher-settings"));
        var followerStore = new SettingsStore(Path.Combine(root, "follower-settings"));
        Directory.CreateDirectory(publisherStore.DirectoryPath); Directory.CreateDirectory(followerStore.DirectoryPath);
        var publisherConfig = Path.Combine(publisherStore.DirectoryPath, "rclone config.conf");
        var followerConfig = Path.Combine(followerStore.DirectoryPath, "rclone config.conf");
        var config = "[cloud]\ntype = alias\nremote = " + remoteRoot + "\n";
        File.WriteAllText(publisherConfig, config); File.WriteAllText(followerConfig, config);
        var remoteProfile = "cloud:profile folder/addons.json";
        var publisherSettings = new AppSettings { WoWPath = MakeWoW(Path.Combine(root, "PublisherWoW")) };
        var record = new ManagedAddon { Name = "Multi", RepositoryUrl = "https://github.com/owner/Multi" };
        publisherSettings.Addons.Add(record);
        var package = Path.Combine(root, "package"); WriteAddon(package, "One", "One", "1.0"); WriteAddon(package, "Two", "Two", "1.0");
        new AddonInstaller(publisherStore).InstallExtracted(publisherSettings, record, new("v1.0", "release", []), new("source", "", true),
            new[] { new AddonFolder(Path.Combine(package, "One"), "One"), new AddonFolder(Path.Combine(package, "Two"), "Two") });
        var publisherDialogs = new FakeDialogs { RcloneResult = new(publisherConfig, remoteProfile, "") };
        using var publisher = new MainViewModel(publisherSettings, publisherStore, publisherDialogs);
        Execute(publisher, publisher.RcloneSetupCommand);
        Assert(publisherSettings.RcloneRemoteProfile == remoteProfile && publisherSettings.RcloneConfigPath == publisherConfig && publisherSettings.SyncIsPublisher, string.Join("\n", publisherDialogs.Errors));
        Execute(publisher, publisher.PublishProfileCommand);
        var remotePath = Path.Combine(remoteRoot, "profile folder", "addons.json");
        Assert(!File.Exists(remotePath), "Cancelling publication must not create a remote profile.");
        publisherDialogs.PublishConfirmed = true;
        Execute(publisher, publisher.PublishProfileCommand);
        Assert(File.Exists(remotePath) && new SyncProfileStore(remotePath).Read().Profile.PublisherId == publisherSettings.SyncPublisherId, "rclone must upload the published profile to the selected remote.");
        var export = new ProfileSyncService(publisherStore).Export(publisherSettings);
        var publishWindow = new ProfilePreviewWindow(export, 0);
        Render(publishWindow, Path.Combine(output, "publish-profile.png")); publishWindow.Close();
        var followerSettings = new AppSettings { WoWPath = MakeWoW(Path.Combine(root, "FollowerWoW")) };
        var followerDialogs = new FakeDialogs { RcloneResult = new(followerConfig, remoteProfile, "") };
        var followerAddOns = Path.Combine(followerSettings.WoWPath, "Interface", "AddOns");
        WriteAddon(followerAddOns, "Local", "Local-only addon", "1");
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
            {
                using var entry = zip.CreateEntry("Repo-v1/" + Path.GetRelativePath(package, file).Replace('\\', '/')).Open();
                using var input = File.OpenRead(file); input.CopyTo(entry);
            }
        using var handler = new ProfileGitHub(zipBytes.ToArray());
        using var follower = new MainViewModel(followerSettings, followerStore, followerDialogs, new GitHubService(handler));
        Execute(follower, follower.RcloneSetupCommand);
        Assert(followerSettings.RcloneRemoteProfile == remoteProfile && followerSettings.RcloneConfigPath == followerConfig && !followerSettings.SyncIsPublisher && !follower.PublishProfileCommand.CanExecute(null), "rclone setup must preserve publisher identity and follower permissions.");
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.LastPlan?.Changes == 1 && handler.Downloads == 0, "Cancelling the rclone sync preview must not download or install addons.");
        var previewWindow = new ProfilePreviewWindow(followerDialogs.LastPlan!, export.Profile);
        Render(previewWindow, Path.Combine(output, "sync-profile.png")); previewWindow.Close();
        followerDialogs.SyncChoice = new(false);
        Execute(follower, follower.SyncProfileCommand);
        Assert(follower.Addons.Count == 3 && follower.Addons.Count(a => a.IsManaged) == 2 && handler.Downloads == 1, string.Join("\n", followerDialogs.Errors));
        var after = SafeFiles.HashFiles(followerAddOns, "");
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.LastPlan?.Changes == 0 && handler.Downloads == 1, "Repeating a current rclone profile sync must not download again.");
        EqualTree(after, SafeFiles.HashFiles(followerAddOns, ""));
        var currentWindow = new ProfilePreviewWindow(followerDialogs.LastPlan!, export.Profile);
        Assert(!((Button)currentWindow.FindName("ApplyButton")).IsEnabled, "A current profile must not enable a redundant sync."); currentWindow.Close();
        var extra = new ManagedAddon { Name = "Extra", RepositoryUrl = "https://github.com/owner/Extra" };
        followerSettings.Addons.Add(extra);
        var extraPackage = Path.Combine(root, "extra-package"); WriteAddon(extraPackage, "Extra", "Extra addon", "1.0");
        new AddonInstaller(followerStore).InstallExtracted(followerSettings, extra, new("v1", "release", []), new("source", "", true),
            new[] { new AddonFolder(Path.Combine(extraPackage, "Extra"), "Extra") });
        Execute(follower, follower.SyncProfileCommand);
        Assert(Directory.Exists(Path.Combine(followerAddOns, "Extra")), "Extra sources must remain unless removal is chosen.");
        var extrasWindow = new ProfilePreviewWindow(followerDialogs.LastPlan!, export.Profile);
        Assert(!((Button)extrasWindow.FindName("ApplyButton")).IsEnabled, "Extra-only plans require choosing removal.");
        ((CheckBox)extrasWindow.FindName("RemoveExtrasBox")).IsChecked = true;
        Assert(((Button)extrasWindow.FindName("ApplyButton")).IsEnabled, "Explicit extra removal must enable sync.");
        Render(extrasWindow, Path.Combine(output, "sync-extras.png")); extrasWindow.Close();
        followerDialogs.SyncChoice = new(true);
        Execute(follower, follower.SyncProfileCommand);
        Assert(!Directory.Exists(Path.Combine(followerAddOns, "Extra")) && Directory.Exists(Path.Combine(followerAddOns, "Local")), "rclone profile removal must preserve unmanaged addons.");
        var exe = Path.Combine(followerStore.DirectoryPath, "Tools", "rclone-" + BundledRclone.Version, "rclone.exe");
        Assert(File.Exists(exe) && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "RCLONE-LICENSE.txt")), "Bundled rclone and its license must extract locally.");
        var setup = new RcloneSetupWindow(followerSettings, followerStore.DirectoryPath);
        Render(setup, Path.Combine(output, "rclone-setup.png"));
        ((Button)setup.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => ((ComboBox)setup.FindName("RemoteBox")).Items.Count > 0);
        Assert(Equals(((ComboBox)setup.FindName("RemoteBox")).SelectedItem, "cloud:"), "The real rclone remote list must be populated.");
        ((Button)setup.FindName("TestButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => ((Button)setup.FindName("SaveButton")).IsEnabled);
        Render(setup, Path.Combine(output, "rclone-connected.png")); setup.Close();
        Assert(TokenProtector.Unprotect(TokenProtector.ProtectExact(" password with spaces ")) == " password with spaces ", "Config passwords must preserve their exact bytes.");
        var loaded = followerStore.Load();
        Assert(loaded.RcloneRemoteProfile == remoteProfile && loaded.RcloneConfigPath == followerConfig &&
            loaded.SyncPublisherId == followerSettings.SyncPublisherId && !loaded.SyncIsPublisher, "rclone connection settings and follower identity must survive restart.");
        followerDialogs.AfterPreview = () =>
        {
            var remoteStore = new SyncProfileStore(remotePath);
            var current = remoteStore.Read();
            remoteStore.Publish(new SyncProfile { PublisherId = current.Profile.PublisherId, Addons = current.Profile.Addons }, current.FileHash);
        };
        var beforeRace = SafeFiles.HashFiles(Path.Combine(followerSettings.WoWPath, "Interface", "AddOns"), "");
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.Errors.Count == 1 && followerDialogs.Errors[0].Contains("changed since preview"), "Remote changes during preview must prevent applying a stale revision.");
        EqualTree(beforeRace, SafeFiles.HashFiles(Path.Combine(followerSettings.WoWPath, "Interface", "AddOns"), ""));
        followerDialogs.AfterPreview = null;
        // A corrupt remote or missing file must stop before addon changes, even with a valid cached profile.
        var before = SafeFiles.HashFiles(Path.Combine(followerSettings.WoWPath, "Interface", "AddOns"), "");
        var cache = new RcloneProfileTransport(new RcloneProcessRunner(exe, followerConfig, ""), followerConfig, remoteProfile, Path.Combine(followerStore.DirectoryPath, "SyncProfiles")).LocalStore;
        var cachedHash = cache.Read().FileHash;
        File.WriteAllText(remotePath, "invalid remote profile");
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.Errors.Count == 2 && cache.Read().FileHash == cachedHash, "A corrupt remote must preserve the last valid cache and fail the sync.");
        File.Delete(remotePath); Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.Errors.Count == 3 && handler.Downloads == 1, "A missing remote must not silently use cached state.");
        EqualTree(before, SafeFiles.HashFiles(Path.Combine(followerSettings.WoWPath, "Interface", "AddOns"), ""));
        Assert(publisherDialogs.Errors.Count == 0, string.Join("\n", publisherDialogs.Errors));
        Console.WriteLine("PASS  Bundled rclone: real local remote publish/fetch, pinned multi-folder install, setup remote list/test, local credentials, persistence and stale-cache protection.");
    }

    private static void VerifyCustomRcloneWorkflow(string root, string output)
    {
        var remoteRoot = Path.Combine(root, "remote storage"); Directory.CreateDirectory(remoteRoot);
        var publisherStore = new SettingsStore(Path.Combine(root, "publisher-settings"));
        var followerStore = new SettingsStore(Path.Combine(root, "follower-settings"));
        Directory.CreateDirectory(publisherStore.DirectoryPath); Directory.CreateDirectory(followerStore.DirectoryPath);
        var publisherConfig = Path.Combine(publisherStore.DirectoryPath, "rclone.conf");
        var followerConfig = Path.Combine(followerStore.DirectoryPath, "rclone.conf");
        var config = "[cloud]\ntype = alias\nremote = " + remoteRoot + "\n";
        File.WriteAllText(publisherConfig, config); File.WriteAllText(followerConfig, config);
        const string remoteProfile = "cloud:WoW/legacy-addons.json";
        var publisherSettings = new AppSettings { WoWPath = MakeWoW(Path.Combine(root, "PublisherWoW")) };
        var publisherAddOns = Path.Combine(publisherSettings.WoWPath, "Interface", "AddOns");
        WriteAddon(publisherAddOns, "MyCustom", "My custom addon", "");
        WriteAddon(publisherAddOns, "DragonUI", "Dragon UI", "");
        WriteAddon(publisherAddOns, "PrivateNotes", "Leave this local", "1");
        new AddonManagement(publisherStore).AssignSource(publisherSettings, "DragonUI", "https://github.com/example/DragonUI");
        var candidates = CustomAddonPackages.Candidates(publisherSettings);
        var options = new PublishOptionsWindow(candidates, []);
        Assert(((CheckBox)options.FindName("IncludeCustomBox")).IsChecked != true && options.SelectedFolders.Count == 0, "Custom uploads must be opt-in.");
        Render(options, Path.Combine(output, "publish-options.png"));
        ((CheckBox)options.FindName("IncludeCustomBox")).IsChecked = true;
        Descendants<Button>(options).Single(b => Equals(b.Content, "Select All")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(options.SelectedFolders.Count == 3, "Select All must include the listed local folders.");
        Descendants<Button>(options).Single(b => Equals(b.Content, "Clear Selection")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(options.SelectedFolders.Count == 0, "Clear Selection must clear the upload choices.");
        foreach (var row in options.Candidates) row.IsSelected = row.Candidate.Folders.Any(f => f is "MyCustom" or "DragonUI");
        var selected = options.SelectedFolders;
        Render(options, Path.Combine(output, "publish-options-custom.png")); options.Close();
        var publisherDialogs = new FakeDialogs { RcloneResult = new(publisherConfig, remoteProfile, ""), PublishFolders = selected };
        using var publisher = new MainViewModel(publisherSettings, publisherStore, publisherDialogs);
        Execute(publisher, publisher.RcloneSetupCommand);
        var before = SafeFiles.HashFiles(publisherAddOns, "");
        Execute(publisher, publisher.PublishProfileCommand);
        var remotePath = Path.Combine(remoteRoot, "WoW", "legacy-addons.json");
        Assert(!File.Exists(remotePath) && !Directory.Exists(remotePath + ".packages"), "Cancelled preview must not upload any custom files.");
        publisherDialogs.PublishConfirmed = true;
        Execute(publisher, publisher.PublishProfileCommand);
        SameCustomTree();
        var profile = new SyncProfileStore(remotePath).Read().Profile;
        Assert(profile.SchemaVersion == 2 && profile.Addons.Count == 2 && profile.Addons.All(a => a.IsCustom), string.Join("\n", publisherDialogs.Errors));
        Assert(Directory.GetFiles(remotePath + ".packages", "*.zip").Length == 2 && publisherDialogs.LastUnmanagedCount == 1, "Only selected custom folders must upload.");
        Assert(publisherStore.Load().CustomPublishFolders.Values.Single().Order().SequenceEqual(selected.Order()), "Custom choices must survive restart.");
        var preview = new ProfilePreviewWindow(publisherDialogs.LastExport!, publisherDialogs.LastUnmanagedCount);
        Render(preview, Path.Combine(output, "publish-custom-preview.png")); preview.Close();
        var remembered = new PublishOptionsWindow(candidates, publisherStore.Load().CustomPublishFolders.Values.Single());
        Assert(remembered.SelectedFolders.Count == 2, "The options dialog must restore saved choices."); remembered.Close();
        var followerSettings = new AppSettings { WoWPath = MakeWoW(Path.Combine(root, "FollowerWoW")) };
        var followerAddOns = Path.Combine(followerSettings.WoWPath, "Interface", "AddOns");
        WriteAddon(followerAddOns, "LocalOnly", "Keep local", "1");
        var wtf = Path.Combine(followerSettings.WoWPath, "WTF"); Directory.CreateDirectory(wtf);
        File.WriteAllText(Path.Combine(wtf, "config.wtf"), "local game settings");
        var followerDialogs = new FakeDialogs { RcloneResult = new(followerConfig, remoteProfile, ""), SyncChoice = new(false) };
        using var handler = new FakeGitHub();
        using var follower = new MainViewModel(followerSettings, followerStore, followerDialogs, new GitHubService(handler));
        Execute(follower, follower.RcloneSetupCommand); Execute(follower, follower.SyncProfileCommand);
        Assert(followerSettings.Addons.Count == 2 && followerSettings.Addons.All(a => a.IsCustom) && handler.Checks == 0 && handler.Downloads == 0, string.Join("\n", followerDialogs.Errors));
        follower.Selected = follower.Addons.Single(a => a.Folder == "MyCustom");
        Assert(follower.Selected.Status == "Installed - Custom Sync" && follower.Selected.Source == "Custom files (rclone)" &&
            !follower.CheckCommand.CanExecute(null) && !follower.UpdateSelectedCommand.CanExecute(null) && !follower.InstallCommand.CanExecute(null), "Custom addons must use profile sync rather than GitHub commands.");
        var followerWindow = new MainWindow { DataContext = follower };
        Render(followerWindow, Path.Combine(output, "main-custom-sync.png")); followerWindow.Close();
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerDialogs.LastPlan!.Changes == 0, "Repeated custom sync must be Current.");
        File.WriteAllText(Path.Combine(publisherAddOns, "MyCustom", "main.lua"), "-- updated custom code");
        Execute(publisher, publisher.PublishProfileCommand); Execute(follower, follower.SyncProfileCommand);
        Assert(File.ReadAllText(Path.Combine(followerAddOns, "MyCustom", "main.lua")) == "-- updated custom code", "Republishing custom edits must update the follower's owned folder.");
        Assert(publisherDialogs.LastPreviousPublishFolders.Count == 2, "Publish command must supply remembered choices.");
        publisherDialogs.PublishFolders = []; Execute(publisher, publisher.PublishProfileCommand);
        Assert(new SyncProfileStore(remotePath).Read().Profile.Addons.Count == 0, "Clearing custom choices must allow an explicitly reviewed empty publication.");
        Execute(follower, follower.SyncProfileCommand);
        Assert(followerSettings.Addons.Count == 2, "Custom extras must be kept by default.");
        followerDialogs.SyncChoice = new(true); Execute(follower, follower.SyncProfileCommand);
        Assert(followerStore.Load().Addons.Count == 0 && Directory.Exists(Path.Combine(followerAddOns, "LocalOnly")) &&
            File.ReadAllText(Path.Combine(wtf, "config.wtf")) == "local game settings", "Explicit custom removal must preserve unrelated addons and WTF settings.");
        Assert(publisherDialogs.Errors.Count == 0 && followerDialogs.Errors.Count == 0, string.Join("\n", publisherDialogs.Errors.Concat(followerDialogs.Errors)));
        Console.WriteLine("PASS  Custom rclone UI workflow: explicit selection, unknown/unmanaged ZIPs, cancel, real transfers, remembered choices, current/update/remove, custom status and unrelated-file protection.");
        void SameCustomTree() => EqualTree(before, SafeFiles.HashFiles(publisherAddOns, ""));
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (DateTime.UtcNow > deadline) throw new TimeoutException("rclone setup action did not finish.");
            Thread.Sleep(5);
        }
    }
    private static string MakeWoW(string path) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "Wow.exe"), "fixture"); return path; }
    private static void WriteAddon(string root, string name, string title, string version)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".toc"), $"## Title: {title}\n## Version: {version}\n## Interface: 30300\n## Author: Example Author\n## Notes: A legacy addon\n## SavedVariables: {name.Replace("-", "")}DB\n## Dependencies: CoreLibrary\n## OptionalDeps: SharedMedia\n");
        File.WriteAllText(Path.Combine(folder, "main.lua"), "-- original local addon files");
    }
    private static void EqualTree(Dictionary<string, string> before, Dictionary<string, string> after) =>
        Assert(before.Count == after.Count && before.All(f => after.TryGetValue(f.Key, out var value) && value == f.Value), "Addon files were changed by a metadata-only operation.");
    private static void Render(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000; window.Top = -20000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var margin = visual.Margin;
        var width = visual.ActualWidth + margin.Left + margin.Right;
        var height = visual.ActualHeight + margin.Top + margin.Bottom;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(margin.Left, margin.Top, visual.ActualWidth, visual.ActualHeight) }, null,
                new Rect(margin.Left, margin.Top, visual.ActualWidth, visual.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public bool Fail { get; set; }
        public bool RateLimited { get; set; }
        public bool WaitForCancellation { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/releases/latest")) Checks++; else Downloads++;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            var response = new HttpResponseMessage(RateLimited ? HttpStatusCode.Forbidden : Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                { Content = new StringContent("{\"tag_name\":\"v1.2.3\",\"assets\":[]}") };
            if (RateLimited) response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        }
    }
    private sealed class FakeDialogs : IUserDialogs
    {
        public string Source { get; set; } = "";
        public string? WoWPath { get; set; }
        public int UpdateConfirmations { get; private set; }
        public List<string> Errors { get; } = [];
        public bool PublishConfirmed { get; set; }
        public ProfileSyncChoice? SyncChoice { get; set; }
        public SyncPlan? LastPlan { get; private set; }
        public RcloneConnectionResult? RcloneResult { get; set; }
        public Action? AfterPreview { get; set; }
        public string? RepositoryUrl() => null;
        public string? EditSource(string name, string source) => Source;
        public string? BrowseWoW(string current) => WoWPath;
        public TokenSettingsResult? Settings(bool hasToken) => null;
        public DownloadOption? ChooseDownload(RepositoryVersion version) => version.Downloads.First();
        public bool ConfirmRemove(ManagedAddon addon) => false;
        public bool ConfirmStopManaging(string folder) => true;
        public bool ConfirmFirstUpdate(ManagedAddon addon) { UpdateConfirmations++; return false; }
        public ProfileExport? LastExport { get; private set; }
        public int LastUnmanagedCount { get; private set; }
        public bool ConfirmPublish(ProfileExport export, int unmanagedCount) { LastExport = export; LastUnmanagedCount = unmanagedCount; return PublishConfirmed; }
        public IReadOnlyList<string>? PublishFolders { get; set; } = [];
        public IReadOnlyCollection<string> LastPreviousPublishFolders { get; private set; } = [];
        public IReadOnlyList<string>? ChoosePublishFiles(IReadOnlyList<LocalPublishCandidate> candidates, IReadOnlyCollection<string> previous)
        { LastPreviousPublishFolders = previous; return PublishFolders; }
        public ProfileSyncChoice? PreviewSync(SyncPlan plan, SyncProfile profile) { LastPlan = plan; AfterPreview?.Invoke(); return SyncChoice; }
        public RcloneConnectionResult? ConfigureRclone(AppSettings settings, string localDirectory) => RcloneResult;
        public void Error(string message) => Errors.Add(message);
    }
    private sealed class ProfileGitHub(byte[] zip) : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert(!path.EndsWith("/latest"), "Sync must use the pinned version.");
            if (path.EndsWith("/releases/tags/v1.0"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"v1.0\",\"assets\":[]}") });
            Assert(path.EndsWith("/zipball/v1.0"), "Unexpected sync download.");
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }
    }
}
