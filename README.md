# Legacy Addon Manager

A dark-mode Windows desktop addon manager for **World of Warcraft 3.3.5a**, built with **C#, .NET 8, WPF, and MVVM**. Manage addons from public GitHub repositories without a GitHub account. The Windows x64 release is one self-contained executable.

**[Download the latest Windows x64 release](https://github.com/QuikSak/LegacyAddonManager/releases/latest)**. Download `LegacyAddonManager.exe` from Assets and run it. No .NET runtime or separate rclone installation is required. See [release notes](RELEASE_NOTES.md), [Google Drive setup](docs/GoogleDriveSetup.md), [Dropbox setup](docs/DropboxSetup.md), and [custom addon sync](docs/CustomAddonSync.md).

## Run

Launch `artifacts\publish\LegacyAddonManager.exe` after publishing. The published app does not require an installed .NET runtime. It runs with your normal Windows account; choose a WoW installation you can write to.

1. Click **Browse** and select the WoW directory containing `Wow.exe`. All installed addon folders appear automatically, including manually installed addons.
2. Select an existing addon, click **Edit Source**, and assign a repository such as `https://github.com/ProfetGit/RefactorGear`. The manager adopts it, saves the association, and checks GitHub without downloading or changing addon files.
3. GitHub versions are checked automatically each time the app opens, after the window appears. Use **Check Selected** or **Check All** to check again. **Update Selected** updates the selected source group. **Update All** updates groups with a known different version; unknown-only groups stay untouched.
4. **Refresh Addons** rescans disk after you manually install, move, or edit an addon. Scanning also runs on startup and after changing the WoW path.
5. **Stop Managing** removes the selected folder's source association while keeping every installed file. **Remove Addon…** is a separate uninstall action that confirms the exact source group's folders before removing them.

To install a new repository, click **Add GitHub Repository**, enter its URL, select its pending entry, and click **Install**. If a release has multiple ZIP assets, choose the package for 3.3.5a. **Open AddOns Folder** opens `<WoWPath>\Interface\AddOns`.

The startup check reads GitHub version metadata for the selected WoW installation without downloading or installing addons. Shared source groups are checked once. Custom and unmanaged addons are skipped; custom files still use **Sync from Profile**. If no WoW path or GitHub sources are configured, the check is skipped. Connection and rate-limit errors appear in the list/status without a startup error dialog; you can retry using **Check All**, or cancel an active check with **Cancel**.

Close WoW before installing, updating, or removing addons. The manager targets 3.3.5a's addon directory layout, but cannot guarantee that arbitrary repository code is compatible with that client. Choose repositories and release packages intended for 3.3.5a. It does not execute downloaded addon code.

## Discovered addons and sources

The AddOns directory is the source of truth for installed addons. Each direct child folder with at least one `.toc` becomes a row; nested library `.toc` files are not separate installed addons. Unmanaged addons do not need database entries. The list merges these live folders with managed source records, so a tracked folder that disappears remains visible as **Missing**.

The table displays **Addon**, **Folder**, **Installed Version**, **Latest Version**, **Source**, and **Status**. The name comes from the `.toc` Title, falling back to the folder name. The installed version comes from `.toc` Version when available; a known manager-installed GitHub version is the fallback. Expand **Addon details** to see Notes, Author, Interface, SavedVariables, SavedVariablesPerCharacter, Dependencies, OptionalDependencies, the TOC filename, tracked GitHub identity, origin, and associated folders. RequiredDeps/OptionalDeps aliases and common WoW title color markup are handled. If several TOCs exist, the folder-matching TOC is preferred, then `_Wrath`, then the first alphabetical TOC.

An addon with no source displays **Installed - Unmanaged** and a blank Source. Assigning a GitHub source adopts that folder: the app saves its display name, local version, installation path, source, origin, folder association, and a SHA-256 file/directory snapshot in JSON. It does not write an ownership marker or alter the existing addon. The initial GitHub request reads release/commit metadata only.

For an adopted folder without a stored GitHub installation identity:

- A local version such as `1.2.3` clearly matches a release tag such as `v1.2.3` and displays **Current**. Numeric versions tolerate a leading `v`, trailing zero components, and SemVer build metadata.
- Two recognizable numeric release versions that differ display **Update Available**. This means the versions differ; it does not assert that your local custom build is older.
- Empty versions, unresolved packaging placeholders, incomparable names, or a repository using commit fallback display **Installed - Version Unknown**. Matching exact non-placeholder release names can also be treated as Current.
- **Checking** identifies active requests. **Error** preserves the source and every installed file when metadata or network access fails. **Missing** preserves the source for a formerly tracked folder absent from the live scan.

Adoption never invents a release tag or SHA for manually installed files. Only a successful manager installation records the downloaded GitHub identity. For folders already installed by the manager, that exact tag/SHA remains the update-comparison authority, independently of the human-readable TOC version.

Right-click any addon row for the same actions available on the buttons: Edit Source, Check Selected, Update Selected, Install, Stop Managing, Remove Addon, Check All, Update All, Refresh Addons, Add GitHub Repository, and Open AddOns Folder. Right-click selects that row first; unavailable actions are disabled using the same command rules as the buttons. The keyboard context-menu key or Shift+F10 also opens the menu for the focused row.

## Multiple folders sharing a source

Assign the same GitHub URL to each installed folder belonging to a project, for example `DBM-Core`, `DBM-GUI`, and `DBM-Icecrown`. They remain individually visible and share one repository record and update operation. The table sorts associated rows together, **Check All** queries that source once, and update actions install its package once for the whole group. The selected row's details list every associated folder.

Folder names alone do not establish a relationship. A downloaded package cannot overwrite an existing folder that you have not associated with its source. Associate all relevant installed folders before the first update. The first update of adopted files asks you to confirm the affected source group and uses the existing staged installation/rollback transaction. Associated folders absent from the downloaded package are removed as part of that explicit update. Unrelated folders remain untouched.

**Update All** skips groups whose installed versions are only unknown or already current. Use **Update Selected** when you explicitly want to replace an inconclusive local version; it still asks for confirmation before the first adopted update.

## Removing tracking versus uninstalling

**Stop Managing**, or saving a blank **Edit Source** field and confirming it, changes management metadata only. It keeps the selected addon folder, all of its files, and saved variables. Other folders sharing the source keep their associations. An installed folder returns to **Installed - Unmanaged**; stopping a missing/pending entry removes that tracking entry. Any old ownership marker stays on disk so the file tree is unchanged. Reassigning a source later safely adopts the folder with a fresh JSON snapshot.

**Remove Addon…** explicitly uninstalls every folder in the selected source group, after listing them in a separate confirmation. It is available for managed/adopted addons, validates ownership/snapshots, and uses rollback. Unmanaged folders cannot be uninstalled through this managed removal action. Source removal, connection failures, or inconclusive versions never trigger uninstallation.

## Sync between computers

For Google Drive, follow the [step-by-step setup guide for both computers](docs/GoogleDriveSetup.md), including Google's OAuth client setup.

For Dropbox, follow the [step-by-step Dropbox setup guide for both computers](docs/DropboxSetup.md).

To share custom or unmanaged addons, follow the [custom addon upload guide](docs/CustomAddonSync.md). Both computers need version **1.4.0 or newer** for custom files.

Use a **shared addon profile** to reproduce the publisher computer's addon versions on a second computer. **Bundled rclone** transfers the JSON profile and optional custom ZIPs directly to your storage provider on demand, without a desktop sync client, mount, background service, or separate rclone installation. GitHub supplies packages for GitHub-version entries; rclone supplies selected custom files. You need a storage account or server supported by rclone, such as Google Drive, Dropbox, S3, WebDAV or SFTP.

### Set up rclone

1. On the publisher computer, click **rclone Setup…**, then **Configure Remote…**. The bundled rclone console wizard opens. Create a named remote, such as `cloud`, and sign in to your storage provider when prompted. Finish and close the wizard. You can also select an existing local `rclone.conf` with **Existing Config…**.
2. Click **Refresh Remotes**, choose your remote, and enter a specific profile path, such as `cloud:WoW/legacy-addons.json`. Remote names must contain at least two characters; paths use forward slashes.
3. Click **Test Connection**, then **Use rclone**. A new remote filename makes this computer the publisher; an existing profile keeps its publisher identity. The test reads only and does not create remote files. Upload permissions are tested when publishing.
4. Click **Publish to Profile**. Optionally enable **Include selected custom / unmanaged addon files** and choose groups to upload, then click **Review Publication**. Review the included versions and folders, then **Publish Profile**. The app uploads and verifies custom ZIPs before uploading and verifying the profile.
5. On the second computer, select its own WoW path, configure its own local rclone remote pointing to the same storage account/location, and choose the same profile path. It connects as a follower. Click **Sync from Profile** to fetch the latest published profile, preview changes, then **Sync Now**.
6. After updating addons on the publisher, publish again and sync the follower. Publication and synchronization are explicit actions, with one publisher per profile.

The default rclone config is `%LocalAppData%\WoWAddonManager\rclone.conf`. Storage credentials are managed by rclone in that local config and are never included in the shared profile. Each PC signs in separately. For an encrypted config, expand **Encrypted config password**; an optional password is protected with Windows DPAPI for your user and passed to the child process through its environment, never on its command line. An existing standard rclone config at `%APPDATA%\rclone\rclone.conf` can be selected explicitly.

Transfers invoke `rclone copyto` for each specific profile or custom ZIP file, with bounded retries/timeouts and cancellation. Remote reads always fetch fresh bytes; an invalid, missing or failed download stops synchronization and preserves the last valid local cache. That cache is **not** used as an offline substitute. The profile is fetched again after preview to detect changes before applying it, and publication checks the remote before overwriting it. Provider stderr is not displayed or persisted because it can contain signed URLs or credentials. These checks do not provide distributed locking: keep one publisher and avoid concurrent publications.

The EXE embeds the official **rclone v1.75.1 Windows x64 ZIP**, whose SHA-256 was verified against the release's official checksum file. On first rclone use it extracts only the executable and license under `%LocalAppData%\WoWAddonManager\Tools\rclone-v1.75.1`; it verifies the embedded ZIP and extracted executable, and checks the executable hash on later use. The helper process exits after each operation. This increases the distributed EXE by the compressed archive size, while keeping idle operation free of a separate sync service. Version, URLs, checksums and license provenance are recorded in `src\LegacyAddonManager\Assets\ThirdParty\rclone-manifest.json`. Rclone is MIT-licensed; its copyright/permission notice is included in `RCLONE-LICENSE.txt` and embedded in the app. See [rclone setup documentation](https://rclone.org/docs/) and [single-file transfers](https://rclone.org/commands/rclone_copyto/).

### Versions and safety

The profile contains repository URLs, exact release tags or full commit SHAs, the selected release ZIP name (or explicit source-archive choice), expected addon folder names, and a SHA-256 fingerprint of the installed file contents. Sync requests those versions instead of `latest`, and may **downgrade** a follower to match the publisher. The downloaded folders and contents must match the profile before any installation transaction starts. Deleted release assets, changed tags/packages, invalid profiles, rate limits, network failures, and local file modifications stop the operation with a clear error; sync does not select an alternative package. A profile that changes since preview must be previewed again.

Complete manager-installed source groups with an established GitHub identity publish their recorded GitHub version. Pending, missing, adopted/unestablished sources and unmanaged addons are excluded unless installed files are selected in publish options. Custom selection uploads the actual installed folders, including local edits, as a verified ZIP; a selected GitHub group publishes its files instead of its GitHub version. Publication does not change the publisher's addon files or invent a GitHub version. Selection is remembered separately for each WoW installation. For stock GitHub publication, **Update Selected** can establish an adopted source's first manager installation; associate all folders and review the replacement confirmation first.

Custom ZIPs are stored beside the profile in `<profile-filename>.packages/<archive-SHA256>.zip`. ZIPs are uploaded and verified before the profile is committed; failures preserve the previous profile. Followers validate both ZIP and content hashes, retain actual folder names, and install through the same ownership/rollback transaction as GitHub addons. Custom sync entries display **Installed - Custom Sync** and receive updates through **Sync from Profile**, rather than GitHub commands. Limits are 512 MB compressed, 2 GB expanded and 100,000 entries per ZIP. Old cloud ZIPs are retained; no cloud files are deleted automatically. Custom profiles use schema 2 and require app 1.4+ on both PCs; GitHub-only schema 1 profiles remain supported.

Extra managed sources are **kept by default**. To make the managed source list match, explicitly check **Also uninstall managed sources absent from this profile** in the sync preview. The preview lists those sources and folders. Their ownership is validated; removal happens only after all requested installations succeed. Unmanaged addons are never removed. A same-name unmanaged folder blocks installation: associate it with the correct source yourself, or back up and move it before retrying.

After removing the last managed source on the publisher, you can publish an empty profile. Followers still keep their addons unless you explicitly select the extra-source removal option.

Each computer keeps its own WoW path, token, publisher identity, ownership IDs and adoption snapshots in its local settings. Do **not** copy `settings.json` between computers. The shared profile excludes credentials and machine paths. One publisher owns each profile; another computer cannot overwrite it. If the publisher's local settings are lost, create a new profile filename and reconnect both computers.

Close WoW before syncing. Changes finish **one source group at a time**; a later failure or cancellation preserves completed groups. Retry Sync from Profile to finish. Existing transaction recovery rules still apply. WoW's `WTF` folder, SavedVariables outside AddOns, UI layouts and unselected unmanaged addons are not transferred; back them up or transfer them separately with WoW closed. Selected addon folders include their actual file contents.

## Build in Visual Studio

- Windows 10/11 x64.
- Visual Studio 2022 17.8 or newer, with **.NET desktop development** and the **.NET 8 SDK/targeting packs**. A newer SDK that supports targeting .NET 8 also works.
- Internet access for first restore and GitHub operations.

Open `LegacyAddonManager.sln`. Set `LegacyAddonManager` as the startup project, then build/run. The solution also contains a portable core library, a dependency-free regression test runner, and a WPF smoke-test executable. No Git, external archiver, MVVM framework, or WebView is required at runtime.

```powershell
.\scripts\GetRclone.ps1
dotnet restore LegacyAddonManager.sln
dotnet build LegacyAddonManager.sln -c Release
dotnet run --project src/LegacyAddonManager/LegacyAddonManager.csproj
```

Development runs target the .NET 8 desktop runtime. Use the self-contained publish if the development computer only has a newer runtime installed.

## Publish one Windows x64 executable

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish.ps1
```

The script downloads/verifies the pinned rclone archive if needed, builds in a fresh staging directory, verifies that publishing produced exactly one executable, and copies it to **`artifacts\publish\LegacyAddonManager.exe`**. Distribute that executable; it includes rclone. The script does not install or launch the app. The downloaded ZIP is excluded from Git and is reused locally when its hash matches. For direct CLI or Visual Studio builds, run `scripts\GetRclone.ps1` first to obtain it.

Equivalent direct command:

```powershell
dotnet publish src/LegacyAddonManager/LegacyAddonManager.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false `
  -o artifacts/publish
```

In Visual Studio, right-click the app project → **Publish** → use the existing **Windows-x64** folder profile. It sets the same self-contained and single-file options. WPF trimming is disabled. Native runtime files extract to the normal .NET bundle cache at launch; distribute only the EXE. See [Microsoft's single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

## Settings and GitHub

Settings are stored atomically in:

```text
%LocalAppData%\WoWAddonManager\settings.json
```

Schema **2** JSON contains the selected WoW path, normalized source URLs, per-installation AddOns paths, installed release tags/full SHAs when known, version kind, installation IDs, folder lists, cached TOC versions/display names, origin/adoption state, baseline snapshots, and selected release asset names. Plain discovered addons are not persisted unnecessarily. Their names, versions, and installed presence are reread from disk. Latest GitHub versions are fetched on startup and on demand. Corrupt or unsupported settings cause a clear startup error and are preserved.

Version **1** settings migrate in memory automatically, preserving repository IDs, installation IDs, folder lists, release/commit history, token, and ownership markers. The next successful save writes schema 2 and first preserves the original JSON as `settings.v1.backup.json` (with a unique suffix if that backup already exists). No addon is reinstalled during migration.

Source associations belong to a particular WoW AddOns path. Changing paths rescans the new installation and preserves other installations' records; switching back restores their associations. A pending repository added before selecting any path becomes bound when it is adopted or installed.

**Settings** accepts an optional personal access token. Public repositories need no token or account. Tokens are encrypted using Windows DPAPI for the current Windows user and are sent only to `api.github.com`, including when archives redirect to download hosts. Leave the token field blank to keep the saved token, or select **Clear the saved token**. For public repositories, a fine-grained token can use read-only public access; private repositories additionally need appropriate read access. If you copy settings to another Windows account, clear or replace the encrypted token.

Every request includes `User-Agent: LegacyAddonManager/1.4`; API requests also include GitHub's JSON Accept and API version headers. Requests are sequential. Rate-limit responses honor `Retry-After` or `X-RateLimit-Reset` and stop further requests until the retry time. The UI shows when to retry and suggests optional token support. See [GitHub's rate-limit documentation](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).

Repository URL validation accepts HTTPS `github.com/owner/repository` URLs, optional trailing slash and `.git`. Subpages, query strings, fragments, other hosts, embedded credentials, and custom ports are rejected.

## Version and archive selection

- Use GitHub's **latest published non-prerelease release**, identified by its exact tag. See [GitHub releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release).
- Prefer a single attached `.zip` asset. If there are multiple ZIP assets, show a package picker; remember the selected asset name for subsequent releases when it is still present. The picker also offers the tag's source archive. With no ZIP assets, use that source archive.
- If the latest-release endpoint returns 404, fetch repository metadata, resolve its **default branch**, and query its latest full commit SHA. Download the archive pinned to that SHA rather than a moving branch name. Repositories without a stable release use this commit workflow, including repositories with only prereleases.
- Authentication, rate-limit, server, and network failures are reported as errors, rather than incorrectly falling back to a different version source. Selecting a repository reveals the full error details.
- Tags are compared exactly; SHAs are stored in full and shortened to 10 characters for display. Publishing different content under the same tag is not detected as a new version.

ZIPs download to a unique temporary directory. Extraction rejects path traversal, symbolic links, Windows reserved/alias names, duplicate file paths, archives over 100,000 entries, downloads over 512 MB, and expanded content over 2 GB. The app recursively searches for `.toc` files and installs their containing addon folders; sibling addon folders are all handled together.

An outer GitHub directory such as `Repo-main` is discarded unless it is itself an addon root with a `.toc`. A root like `RefactorGear-main/RefactorGear.toc` installs as `RefactorGear`, matching WoW's addon naming convention. Recognized flavor suffixes such as `_Wrath` are stripped when resolving the base addon name. Nested `.toc` libraries stay inside their parent addon. Ambiguous `.toc` names or duplicate destination names cause an error; use a packaged release ZIP in those cases. Source archives do not fetch Git submodules or Git LFS objects.

## Folder ownership and recovery

Each manager-installed root contains `.legacy-addon-manager.json`, recording its repository record ID, installation ID, and SHA-256 hashes of installed files and directory entries. Keep this file: it proves which installation owns that folder. Adopted roots instead use their JSON baseline until the first successful manager update, which creates the usual markers.

- Existing unmanaged folders are never automatically adopted, overwritten, or deleted. Adoption requires assigning a source. A collision with an unassociated folder aborts the whole package before changing existing addons.
- Updates and uninstall require the tracked folder name plus a matching ownership marker or unchanged adoption snapshot. Another managed source cannot claim the same folder within the same WoW installation.
- Changes since adoption/installation, added files, and added directories block replacement/uninstall. Back up and move the modified folder outside `AddOns` yourself, then retry. **Install** restores missing managed folders. They display **Missing** until restored. **Stop Managing** remains available even if a folder has local modifications.
- Updates replace the complete owned folder, which removes stale files and addon roots no longer shipped by that repository. Your `WTF` saved variables and all unmanaged addon roots are preserved.
- Installations and removal stage changes in `.lam-transaction-<id>` within `AddOns`, keeping backups on the same volume. If copying, moving, or saving settings fails, the operation rolls back. Downloads/extraction can be cancelled; once folder changes begin, the small transaction finishes or rolls back before cancellation can take effect.
- One app instance is permitted per Windows session/user. Installation paths containing junctions or symbolic links are refused to prevent writes outside the selected directory. Records from other WoW paths cannot update or uninstall files in the currently selected installation.

If the app or Windows stops during a transaction, or rollback/cleanup cannot complete, backups and `recovery.json` are preserved. Further installs/removals stop while a `.lam-transaction-*` directory exists. Automatic recovery after power loss is intentionally not attempted.

To recover, close the manager and WoW and **make a copy of the transaction directory, current addon folders, and settings JSON first**. Inspect `recovery.json` and the current settings:

1. An install is committed if that repository's settings record has `InstallationId` equal to `NewInstallationId`. A removal is committed if its `AddonId` no longer exists in settings. If committed, retain the new/current addon folders; after checking your backup, move the transaction directory outside `AddOns`.
2. If uncommitted, move aside any partially installed new folders only after checking their marker against `NewInstallationId`. Move the folders in the transaction's `backup` directory back into `AddOns`; never overwrite an existing folder while doing this.
3. If settings need restoration, write the `SettingsBefore` object from `recovery.json` back to `settings.json`. Keep the backup copy of the original JSON.
4. Move the transaction directory outside `AddOns`, relaunch, and check versions. Retain your recovery copy until the addons work in WoW.

## Verify

With .NET 8 runtimes installed:

```powershell
dotnet run --project tests/LegacyAddonManager.Tests -c Release
dotnet run --project tests/LegacyAddonManager.Tests -c Release -- --live
dotnet run --project tests/LegacyAddonManager.UiSmoke -c Release
```

The 30 core regressions exercise the original archive/install/network behavior plus live unmanaged discovery, TOC metadata, adoption without file changes, current/different/unknown comparisons, shared repositories, staged first updates and rollback, snapshot validation, safe stop-managing and reassignment, path-scoped installations, Missing records, cancellation, and version 1 migration. `--live` additionally downloads, installs, and removes `ProfetGit/RefactorGear` in an isolated temporary fake WoW installation. It never touches your actual WoW folder.

The UI smoke runner renders the main window at normal/minimum sizes and all dialogs offscreen, checks URL validation and command availability, and tests DPAPI encryption. It also executes the MVVM workflow using isolated manual addon fixtures and stub GitHub responses: startup discovery, Edit Source with unchanged file hashes, grouped checks, unknown-version preservation, first-update confirmation, network-error preservation, Stop Managing with unchanged file hashes, restart, Refresh, Missing records, and WoW path changes. PNGs are saved under `artifacts\screenshots`. With only a newer runtime installed, publish either test project using `-r win-x64 --self-contained true -o artifacts/tests` (use `artifacts/ui-tests` for UiSmoke) and run its EXE.

Profile regression tests cover two independent installations, pinned older releases and commits, multiple addon folders, explicit source/asset selection, content mismatch rejection, profile changes before and during downloads, publisher conflicts, unmanaged/modified-folder protection, empty profiles, optional removal, cancellation, rollback and persistence. The UI workflow tests publish and sync previews, cancellation, follower command availability, repeated Current syncs and optional extra-source removal. `--live` also syncs the downloaded GitHub addon into a second isolated WoW installation and verifies its contents match the published profile.

Rclone tests cover literal process arguments, bounded output, environment isolation, process cancellation/timeouts, remote validation, fresh downloads, invalid/missing profiles, failed uploads, verification and publisher conflicts. The UI smoke runner also extracts the bundled binary and executes **real rclone** against a temporary local alias remote: two PCs' independent settings, publish/upload/fetch, pinned multi-addon installation, the setup remote dropdown/connection test, restart, and stale-cache protection. This tests the actual CLI integration without accessing your cloud accounts or actual WoW installation.

Nine additional custom-sync regressions cover explicit selection, unknown sources, modified manager files, deterministic ZIPs, empty directories and folder naming, no-GitHub roundtrips, corruption, unsafe metadata, upload failure, local ownership, concurrent profile changes, rollback/cancellation and optional removal. The UI runner uses the real shared dark theme without production startup, so it can run alongside an open app. It also exercises real rclone custom uploads/downloads, remembered choices and custom status/command behavior.

Automatic startup-check regressions verify one check per launch, shared source groups, Current/Update Available comparison, custom/unmanaged and other-installation exclusions, unconfigured/invalid paths, cancellation, nonmodal network/rate-limit failures, manual retry, and unchanged addon files/settings with no downloads.

## Project layout

```text
LegacyAddonManager.sln
src/LegacyAddonManager.Core/    Live inventory, TOC parsing, source adoption, comparisons,
                              GitHub, archive handling, ownership, transactions, JSON
src/LegacyAddonManager/         WPF views, MVVM commands/view models, DPAPI
tests/LegacyAddonManager.Tests/ Core regression and opt-in live integration runner
tests/LegacyAddonManager.UiSmoke/ Offscreen WPF rendering and UI checks
scripts/Publish.ps1             Single-EXE Windows x64 publish
```
