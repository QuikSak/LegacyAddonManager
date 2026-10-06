# Legacy Addon Manager 1.4.1

Initial GitHub release for Windows 10/11 x64 and World of Warcraft 3.3.5a.

- Automatically check managed GitHub addon versions when the main window opens. Checks are cancellable and report connection errors and rate limits without blocking startup with a dialog.
- Discover every installed addon, including custom and unmanaged folders, and display TOC names and versions.
- Manage arbitrary GitHub repositories using their latest release or default-branch commit. Install multi-folder packages, check versions, update, and safely remove managed addons.
- Access the same addon actions through toolbar buttons or the row's right-click menu.
- Sync pinned GitHub versions between computers using bundled rclone. Google Drive and Dropbox setup guides are included.
- Optionally publish selected custom, unknown-version, or unmanaged addon files as verified ZIP packages for profile sync.
- Protect unrelated addon folders and local changes through ownership records, collision checks, and rollback.

Download `LegacyAddonManager.exe` under the GitHub release's Assets. It is one self-contained Windows x64 executable with .NET 8 and rclone included. `SHA256SUMS.txt` contains its SHA-256 checksum.

Startup checks read version metadata only. Use the update buttons to install updates. Custom files use Sync from Profile to check for changes. Close WoW before changing addon files.

Validation: Release build with no warnings or errors, 62 core regression tests, WPF UI workflows, automatic startup checks, and real rclone custom/profile transfers.
