using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using LegacyAddonManager.Core;
using LegacyAddonManager.Services;

namespace LegacyAddonManager.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings settings;
    private readonly SettingsStore store;
    private readonly GitHubService github;
    private readonly AddonInstaller installer;
    private readonly AddonManagement management;
    private readonly ProfileSyncService profileSync;
    private readonly AddonInventory inventory = new();
    private readonly IUserDialogs dialogs;
    private readonly Dictionary<Guid, RepositoryVersion> latestVersions = [];
    private readonly Dictionary<Guid, string> operationErrors = [];
    private CancellationTokenSource? cancellation;
    private Task? startupCheck;
    private readonly List<RelayCommand> commands = [];
    public ObservableCollection<AddonViewModel> Addons { get; } = [];
    private AddonViewModel? selected;
    public AddonViewModel? Selected { get => selected; set { Set(ref selected, value); RefreshCommands(); } }
    private bool isBusy;
    public bool IsBusy { get => isBusy; private set { Set(ref isBusy, value); Notify(nameof(IsIdle)); RefreshCommands(); } }
    public bool IsIdle => !IsBusy;
    public string WoWPath => string.IsNullOrEmpty(settings.WoWPath) ? "Choose your World of Warcraft installation…" : settings.WoWPath;
    private bool HasRcloneConnection => settings.RcloneConfigPath.Length > 0 && settings.RcloneRemoteProfile.Length > 0;
    public string SyncDescription => HasRcloneConnection
        ? $"{(settings.SyncIsPublisher ? "Publisher" : "Follower")} · rclone · {settings.RcloneRemoteProfile}"
        : "Sync: set up rclone to connect your computers.";
    private string message = "Choose a WoW installation to discover its addons.";
    public string Message { get => message; private set => Set(ref message, value); }
    public string Summary => $"{Addons.Count(a => a.IsInstalled)} installed  ·  {ActiveRecords().Count()} sources  ·  {Addons.Count(a => a.IsInstalled && !a.IsManaged)} unmanaged";
    public RelayCommand BrowseCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand EditSourceCommand { get; }
    public RelayCommand StopManagingCommand { get; }
    public RelayCommand CheckSelectedCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand UpdateSelectedCommand { get; }
    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand PublishProfileCommand { get; }
    public RelayCommand SyncProfileCommand { get; }
    public RelayCommand RcloneSetupCommand { get; }

    public MainViewModel(AppSettings settings, SettingsStore store, IUserDialogs? dialogs = null, GitHubService? github = null)
    {
        this.settings = settings;
        this.store = store;
        this.dialogs = dialogs ?? new DialogService();
        this.github = github ?? new GitHubService();
        installer = new(store);
        management = new(store);
        profileSync = new(store);
        BrowseCommand = Command(() => _ = RunAsync(BrowseAsync));
        AddCommand = Command(() => _ = RunAsync(AddAsync));
        RefreshCommand = Command(() => _ = RunAsync(RefreshAsync), () => !string.IsNullOrEmpty(settings.WoWPath));
        EditSourceCommand = Command(() => _ = RunAsync(EditSourceAsync), () => Selected?.IsInstalled == true);
        StopManagingCommand = Command(() => _ = RunAsync(StopManagingAsync), () => Selected?.IsManaged == true);
        CheckSelectedCommand = Command(() => _ = RunAsync(CheckSelectedAsync), () => Selected?.Model is { IsCustom: false });
        CheckCommand = Command(() => _ = RunAsync(CheckAllAsync), () => ActiveRecords().Any(a => !a.IsCustom));
        InstallCommand = Command(() => _ = RunAsync(ct => InstallRecordAsync(Selected!.Model!, false, ct)), () => Selected is { Model.IsCustom: false, IsInstalled: false });
        UpdateSelectedCommand = Command(() => _ = RunAsync(ct => InstallRecordAsync(Selected!.Model!, true, ct)),
            () => Selected?.Model is { IsCustom: false } && RowsFor(Selected.Model!).Any(a => a.IsInstalled));
        UpdateAllCommand = Command(() => _ = RunAsync(UpdateAllAsync), () => ActiveRecords().Any(a => !a.IsCustom && RowsFor(a).Any(r => r.IsInstalled)));
        RemoveCommand = Command(() => _ = RunAsync(RemoveAsync), () => Selected?.IsManaged == true && RowsFor(Selected.Model!).Any(a => a.IsInstalled));
        OpenFolderCommand = Command(OpenFolder, () => !string.IsNullOrEmpty(settings.WoWPath));
        SettingsCommand = Command(OpenSettings);
        RcloneSetupCommand = Command(() => _ = RunAsync(ConfigureRcloneAsync));
        PublishProfileCommand = Command(() => _ = RunAsync(PublishProfileAsync),
            () => settings.SyncIsPublisher && HasRcloneConnection && settings.WoWPath.Length > 0);
        SyncProfileCommand = Command(() => _ = RunAsync(SyncProfileAsync),
            () => HasRcloneConnection && settings.WoWPath.Length > 0);
        CancelCommand = new(() => { cancellation?.Cancel(); Message = "Cancelling… an active folder transaction will finish safely."; RefreshCommands(); },
            () => IsBusy && cancellation?.IsCancellationRequested == false);
        commands.Add(CancelCommand);
        try
        {
            ApplyInventory(inventory.Read(settings));
            if (!string.IsNullOrEmpty(settings.WoWPath)) Message = "Addons discovered. Assign a source to manage an existing addon.";
        }
        catch (Exception ex)
        {
            foreach (var record in ActiveRecords())
                foreach (var folder in record.InstalledFolders.DefaultIfEmpty())
                    Addons.Add(new(new(folder, null, record)) { Status = "Error", Detail = FriendlyError(ex) });
            Selected = Addons.FirstOrDefault();
            Message = FriendlyError(ex);
        }
        try { this.github.Token = TokenProtector.Unprotect(settings.ProtectedGitHubToken); }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { Message = "The saved token cannot be decrypted for this Windows user. Clear or replace it in Settings; public access is available."; }
    }

    private IEnumerable<ManagedAddon> ActiveRecords() => settings.Addons.Where(a => ManagementPaths.AppliesTo(a, settings.WoWPath));
    private AddonViewModel[] RowsFor(ManagedAddon record) => Addons.Where(a => a.Model?.Id == record.Id).ToArray();
    private RelayCommand Command(Action execute, Func<bool>? can = null)
    {
        var command = new RelayCommand(execute, () => IsIdle && (can?.Invoke() ?? true));
        commands.Add(command);
        return command;
    }
    private void RefreshCommands() { foreach (var command in commands) command.Refresh(); Notify(nameof(Summary)); }

    // Called after the main window renders; repeated render events share the same check.
    public Task CheckUpdatesOnStartupAsync()
    {
        if (startupCheck != null) return startupCheck;
        if (string.IsNullOrWhiteSpace(settings.WoWPath) || !ActiveRecords().Any(a => !a.IsCustom))
            return startupCheck = Task.CompletedTask;
        Message = "Checking addon versions...";
        return startupCheck = RunAsync(CheckAllAsync, showErrorDialog: false);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool showErrorDialog = true)
    {
        if (IsBusy) return;
        cancellation = new();
        IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Message = cancellation.IsCancellationRequested ? "Operation cancelled." : "The connection timed out. Please try again."; }
        catch (Exception ex) { Message = FriendlyError(ex); if (showErrorDialog) dialogs.Error(Message); }
        finally { cancellation.Dispose(); cancellation = null; IsBusy = false; }
    }

    private void ApplyInventory(InventorySnapshot snapshot, string? selectFolder = null, Guid? selectRecord = null)
    {
        if (selectFolder == null && selectRecord == null)
        {
            selectFolder = Selected?.Entry.FolderName;
            selectRecord = Selected?.Model?.Id;
        }
        Addons.Clear();
        foreach (var entry in snapshot.Entries)
        {
            var row = new AddonViewModel(entry);
            if (row.Model != null && latestVersions.TryGetValue(row.Model.Id, out var latest)) row.Latest = latest;
            row.RefreshStatus();
            if (row.Model != null && entry.Local?.Error == null && operationErrors.TryGetValue(row.Model.Id, out var error))
            { row.Status = "Error"; row.Detail = error; }
            Addons.Add(row);
        }
        Selected = Addons.FirstOrDefault(a => selectFolder != null && a.Entry.FolderName?.Equals(selectFolder, StringComparison.OrdinalIgnoreCase) == true)
            ?? Addons.FirstOrDefault(a => a.Model?.Id == selectRecord) ?? Addons.FirstOrDefault();
        RefreshCommands();
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        Message = "Scanning Interface\\AddOns…";
        var snapshot = await Task.Run(() => inventory.Read(settings, ct), ct);
        ApplyInventory(snapshot);
        Message = snapshot.Warnings.Count == 0 ? $"Refresh complete. {Summary}." : $"Scan complete with {snapshot.Warnings.Count} folder errors. Select an Error row for details.";
    }

    private async Task ReloadAsync(CancellationToken ct, string? folder = null, Guid? record = null)
    {
        var snapshot = await Task.Run(() => inventory.Read(settings, ct), ct);
        ApplyInventory(snapshot, folder, record);
    }

    private async Task BrowseAsync(CancellationToken ct)
    {
        var path = dialogs.BrowseWoW(settings.WoWPath);
        if (path == null) return;
        management.SetWoWPath(settings, path);
        Notify(nameof(WoWPath));
        // Associations for other WoW paths stay in the database and return when that path is selected.
        await RefreshAsync(ct);
    }

    private async Task PublishProfileAsync(CancellationToken ct)
    {
        Message = "Verifying installed addons for the shared profile…";
        var transport = await CreateTransportAsync(ct);
        var before = await transport.ReadAsync(true, ct);
        if (before != null && before.Profile.PublisherId != settings.SyncPublisherId)
            throw new IOException("This profile belongs to another publisher. Reconnect through rclone Setup as a follower, or use a new remote profile filename.");
        var candidates = await Task.Run(() => CustomAddonPackages.Candidates(settings, ct), ct);
        var selectionKey = ManagementPaths.ForWoW(settings.WoWPath)!;
        var previous = settings.CustomPublishFolders.GetValueOrDefault(selectionKey) ?? [];
        var selectedFiles = dialogs.ChoosePublishFiles(candidates, previous);
        if (selectedFiles == null) { Message = "Profile publication cancelled."; return; }
        var work = SafeFiles.Child(Path.GetTempPath(), ".lam-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Message = "Preparing addon versions and selected custom ZIPs…";
            var export = await Task.Run(() => profileSync.Export(settings, ct, selectedFiles, work), ct);
            var snapshot = await Task.Run(() => inventory.Read(settings, ct), ct);
            var includedFolders = export.Profile.Addons.SelectMany(a => a.Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unmanaged = snapshot.Entries.Count(e => e.Local != null && e.Management == null && !includedFolders.Contains(e.FolderName!));
            if (!dialogs.ConfirmPublish(export, unmanaged)) { Message = "Profile publication cancelled."; return; }
            ct.ThrowIfCancellationRequested();
            Message = "Uploading and verifying addon profile and custom ZIPs…";
            await transport.PublishAsync(export.Profile, before?.FileHash, ct, export.Packages);
            var hadSelection = settings.CustomPublishFolders.ContainsKey(selectionKey);
            settings.CustomPublishFolders[selectionKey] = selectedFiles.ToList();
            try { store.Save(settings); }
            catch (Exception ex)
            {
                if (hadSelection) settings.CustomPublishFolders[selectionKey] = previous; else settings.CustomPublishFolders.Remove(selectionKey);
                throw new IOException("The profile uploaded successfully, but the local publish selection could not be saved. " + ex.Message, ex);
            }
            Message = $"Published {export.Profile.Addons.Count(a => !a.IsCustom)} GitHub groups and {export.Profile.Addons.Count(a => a.IsCustom)} custom groups. " +
                $"{unmanaged} unmanaged addons excluded. Upload verified. Use Sync from Profile on the other computer.";
        }
        finally { SafeFiles.DeleteOwnedWorkDirectory(work, Path.GetTempPath()); }
    }

    private async Task SyncProfileAsync(CancellationToken ct)
    {
        Message = "Reading shared profile and comparing installed addons…";
        var transport = await CreateTransportAsync(ct);
        var snapshot = (await transport.ReadAsync(false, ct))!;
        var plan = await Task.Run(() => profileSync.Preview(settings, snapshot.Profile, ct), ct);
        var choice = dialogs.PreviewSync(plan, snapshot.Profile);
        if (choice == null) { Message = "Profile sync cancelled. Installed addons were preserved."; return; }
        int completed;
        try
        {
            var progress = new Progress<string>(detail => Message = detail);
            var current = (await transport.ReadAsync(false, ct))!;
            if (current.FileHash != snapshot.FileHash) throw new IOException("The profile changed since preview. Preview it again before syncing.");
            completed = await profileSync.ApplyAsync(settings, transport.LocalStore, snapshot, choice.RemoveExtras, github, progress, ct, transport);
            foreach (var item in plan.Items.Where(i => i.Action != "Current"))
            {
                if (item.Local != null) { latestVersions.Remove(item.Local.Id); operationErrors.Remove(item.Local.Id); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new IOException($"Sync stopped: {FriendlyError(ex)} Completed source changes remain installed; retry sync to finish.", ex); }
        finally { await ReloadAsync(CancellationToken.None); }
        Message = $"Profile sync complete. {completed} source groups changed. " +
            (choice.RemoveExtras ? "Extra managed sources removed. " : $"{plan.Extras.Count} extra managed sources preserved. ") + "Unmanaged addons and WoW settings were preserved.";
    }

    private async Task<RcloneProfileTransport> CreateTransportAsync(CancellationToken ct)
    {
        if (!HasRcloneConnection) throw new InvalidOperationException("Connect through rclone Setup before publishing or syncing.");
        var executable = await new BundledRclone(store.DirectoryPath).EnsureAvailableAsync(ct);
        var runner = new RcloneProcessRunner(executable, settings.RcloneConfigPath, TokenProtector.Unprotect(settings.ProtectedRclonePassword));
        return new RcloneProfileTransport(runner, settings.RcloneConfigPath, settings.RcloneRemoteProfile, Path.Combine(store.DirectoryPath, "SyncProfiles"));
    }

    private async Task ConfigureRcloneAsync(CancellationToken ct)
    {
        var result = dialogs.ConfigureRclone(settings, store.DirectoryPath);
        if (result == null) return;
        Message = "Connecting to the rclone profile…";
        var executable = await new BundledRclone(store.DirectoryPath).EnsureAvailableAsync(ct);
        var runner = new RcloneProcessRunner(executable, result.ConfigPath, TokenProtector.Unprotect(result.ProtectedPassword));
        var transport = new RcloneProfileTransport(runner, result.ConfigPath, result.RemoteProfile, Path.Combine(store.DirectoryPath, "SyncProfiles"));
        var snapshot = await transport.ReadAsync(true, ct);
        var before = (settings.SyncIsPublisher,
            settings.RcloneConfigPath, settings.RcloneRemoteProfile, settings.ProtectedRclonePassword);
        settings.SyncIsPublisher = snapshot == null || snapshot.Profile.PublisherId == settings.SyncPublisherId;
        settings.RcloneConfigPath = result.ConfigPath;
        settings.RcloneRemoteProfile = result.RemoteProfile;
        settings.ProtectedRclonePassword = result.ProtectedPassword;
        try { store.Save(settings); }
        catch
        {
            (settings.SyncIsPublisher,
                settings.RcloneConfigPath, settings.RcloneRemoteProfile, settings.ProtectedRclonePassword) = before;
            throw;
        }
        Notify(nameof(SyncDescription)); RefreshCommands();
        Message = settings.SyncIsPublisher ? "rclone publisher connected. Publish to Profile uploads your addon versions."
            : "rclone follower connected. Sync from Profile fetches and previews the publisher's addon versions.";
    }

    private async Task AddAsync(CancellationToken ct)
    {
        var input = dialogs.RepositoryUrl();
        if (input == null) return;
        var record = management.AddRepository(settings, input);
        await ReloadAsync(ct, record: record.Id);
        await CheckRecordAsync(record, ct);
        if (RowsFor(record).All(r => r.Status != "Error")) Message = $"Added {record.Name}. Select Install when ready.";
    }

    private async Task EditSourceAsync(CancellationToken ct)
    {
        var row = Selected!;
        var input = dialogs.EditSource(row.Name, row.Model?.RepositoryUrl ?? "");
        if (input == null) return;
        if (string.IsNullOrWhiteSpace(input))
        {
            if (row.Model != null) await StopManagingAsync(ct);
            return;
        }
        Message = $"Adopting {row.Folder} without changing files…";
        ManagedAddon record;
        try { record = await Task.Run(() => management.AssignSource(settings, row.Folder, input, ct), ct); }
        finally { await ReloadAsync(CancellationToken.None, row.Entry.FolderName); }
        await CheckRecordAsync(record, ct);
        if (RowsFor(record).All(r => r.Status != "Error")) Message = $"Source saved for {row.Folder}. Installed files were preserved.";
    }

    private async Task StopManagingAsync(CancellationToken ct)
    {
        var row = Selected!;
        if (row.Model == null || !dialogs.ConfirmStopManaging(row.Entry.FolderName ?? row.Name)) return;
        try { await Task.Run(() => management.StopManaging(settings, row.Model, row.Entry.FolderName), ct); }
        finally { await ReloadAsync(CancellationToken.None, row.Entry.FolderName); }
        Message = $"Stopped managing {row.Folder}. All addon files were preserved.";
    }

    private async Task<bool> CheckRecordAsync(ManagedAddon record, CancellationToken ct)
    {
        operationErrors.Remove(record.Id);
        var rows = RowsFor(record);
        foreach (var row in rows) { row.Status = "Checking"; row.Detail = "Checking GitHub…"; }
        Message = $"Checking {record.RepositoryUrl}…";
        try
        {
            var version = await github.GetLatestAsync(GitHubRepository.Parse(record.RepositoryUrl), ct);
            latestVersions[record.Id] = version;
            foreach (var row in rows) { row.Latest = version; row.RefreshStatus(); }
            Message = $"Checked {record.RepositoryUrl}. {rows.Count(r => r.HasUpdate)} folders have a different version.";
            return true;
        }
        catch (OperationCanceledException) { foreach (var row in rows) row.RefreshStatus(); throw; }
        catch (Exception ex)
        {
            operationErrors[record.Id] = FriendlyError(ex);
            foreach (var row in rows) { row.Status = "Error"; row.Detail = FriendlyError(ex); }
            Message = FriendlyError(ex);
            if (ex is GitHubRateLimitException) throw;
            return false;
        }
    }

    private async Task CheckSelectedAsync(CancellationToken ct)
    {
        var record = Selected!.Model!;
        await ReloadAsync(ct, record: record.Id);
        await CheckRecordAsync(record, ct);
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        await ReloadAsync(ct);
        foreach (var record in ActiveRecords().Where(a => !a.IsCustom).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            await CheckRecordAsync(record, ct);
        }
        Message = $"Check complete. {Addons.Count(a => a.HasUpdate)} folders have updates; {Addons.Count(a => a.Status == "Installed - Version Unknown")} unknown versions; {Addons.Count(a => a.Status == "Error")} errors.";
    }

    private async Task<bool> InstallRecordAsync(ManagedAddon record, bool onlyUpdate, CancellationToken ct, bool skipUnknown = false)
    {
        _ = AddonInstaller.GetAddOnsPath(settings.WoWPath);
        await ReloadAsync(ct, record: record.Id);
        try
        {
            if (!await CheckRecordAsync(record, ct)) return false;
            var rows = RowsFor(record);
            if (onlyUpdate && !rows.Any(r => r.IsInstalled))
            { Message = $"{record.Name} is missing. Use Install to restore its folders."; return false; }
            if (onlyUpdate && record.AdoptedFolders.Count == 0 && rows.Where(r => r.IsInstalled).All(r => r.Status == "Current"))
            { Message = $"{record.Name} is current."; return false; }
            if (onlyUpdate && skipUnknown && !rows.Any(r => r.HasUpdate)) return false;
            var version = latestVersions[record.Id];
            var assets = version.Downloads.Where(d => !d.IsSourceArchive).ToArray();
            var download = version.Downloads.FirstOrDefault(d => d.Name == record.PreferredAssetName && !d.IsSourceArchive);
            download ??= assets.Length switch
            {
                0 => version.Downloads.First(d => d.IsSourceArchive),
                1 => assets[0],
                _ => dialogs.ChooseDownload(version)
            };
            if (download == null) { Message = "Installation cancelled."; return false; }
            if (record.AdoptedFolders.Count > 0 && !dialogs.ConfirmFirstUpdate(record)) { Message = "Adopted files were preserved; update cancelled."; return false; }
            var progress = new Progress<string>(detail =>
            {
                foreach (var row in RowsFor(record)) row.Detail = detail;
                Message = $"{record.Name}: {detail}";
            });
            await installer.InstallAsync(settings, record, version, download, github, progress, ct);
            await ReloadAsync(CancellationToken.None, record: record.Id);
            Message = installer.CleanupWarning ?? $"Installed {version.Display} into {string.Join(", ", record.InstalledFolders)}.";
            return true;
        }
        catch (OperationCanceledException) { foreach (var row in RowsFor(record)) row.RefreshStatus(); throw; }
        catch (Exception ex)
        {
            operationErrors[record.Id] = FriendlyError(ex);
            foreach (var row in RowsFor(record)) { row.Status = "Error"; row.Detail = FriendlyError(ex); }
            throw;
        }
    }

    private async Task UpdateAllAsync(CancellationToken ct)
    {
        await ReloadAsync(ct);
        var records = ActiveRecords().Where(a => !a.IsCustom && RowsFor(a).Any(r => r.IsInstalled)).ToArray();
        var failures = new List<string>();
        var updated = 0;
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await InstallRecordAsync(record, true, ct, skipUnknown: true))
                {
                    updated++;
                    if (installer.CleanupWarning != null) { failures.Add(installer.CleanupWarning); break; }
                }
                if (RowsFor(record).Any(r => r.Status == "Error")) failures.Add($"{record.Name}: {RowsFor(record).First(r => r.Status == "Error").Detail}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not GitHubRateLimitException)
            { failures.Add($"{record.Name}: {FriendlyError(ex)}"); }
        }
        Message = $"Updated {updated} source groups. {failures.Count} errors. Unknown-only groups are preserved; use Update Selected to replace them.";
        if (failures.Count > 0) dialogs.Error(string.Join("\n\n", failures));
    }

    private async Task RemoveAsync(CancellationToken ct)
    {
        var record = Selected!.Model!;
        if (!dialogs.ConfirmRemove(record)) return;
        Message = $"Uninstalling {record.Name}…";
        try { await Task.Run(() => installer.Remove(settings, record), ct); }
        catch (Exception ex)
        {
            operationErrors[record.Id] = FriendlyError(ex);
            foreach (var row in RowsFor(record)) { row.Status = "Error"; row.Detail = FriendlyError(ex); }
            throw;
        }
        await ReloadAsync(CancellationToken.None);
        Message = installer.CleanupWarning ?? $"Uninstalled {record.Name}. Other addons and saved variables were preserved.";
    }

    private void OpenFolder()
    {
        try
        {
            var path = AddonInstaller.GetAddOnsPath(settings.WoWPath);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
        }
        catch (Exception ex) { dialogs.Error(FriendlyError(ex)); }
    }

    private void OpenSettings()
    {
        var result = dialogs.Settings(settings.ProtectedGitHubToken.Length > 0);
        if (result == null || (!result.ClearToken && string.IsNullOrWhiteSpace(result.Token))) return;
        try
        {
            var previous = settings.ProtectedGitHubToken;
            settings.ProtectedGitHubToken = result.ClearToken ? "" : TokenProtector.Protect(result.Token);
            try { store.Save(settings); } catch { settings.ProtectedGitHubToken = previous; throw; }
            github.Token = result.ClearToken ? "" : result.Token.Trim();
            github.ResetRateLimit();
            Message = result.ClearToken ? "Token cleared. Public GitHub access is enabled." : "GitHub token saved securely for this Windows user.";
        }
        catch (Exception ex) { dialogs.Error(FriendlyError(ex)); }
    }

    private static string FriendlyError(Exception ex) => ex is HttpRequestException && ex.InnerException != null
        ? "Could not connect to GitHub. Check your internet connection and try again." :
        ex is UnauthorizedAccessException ? "Access denied. Choose a writable WoW folder or check folder permissions." : ex.Message;
    public void Dispose() { cancellation?.Cancel(); github.Dispose(); }
}
