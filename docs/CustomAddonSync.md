# Publish custom, unknown-version, and unmanaged addons

Use **Legacy Addon Manager 1.4.0 or newer on both computers**. Your existing rclone remote and profile path still work. Dropbox and Google Drive use the same workflow below.

GitHub groups normally publish an exact release or commit, and the follower downloads that version from GitHub. Custom addons can instead publish their actual installed files as ZIPs to your configured rclone storage. They do not need a GitHub source or a known TOC version.

## On your publisher computer

1. Close WoW and click **Refresh Addons** if you added or edited files manually.
2. Click **Publish to Profile**.
3. In **Choose what to publish**, check **Include selected custom / unmanaged addon files**.
4. Check the addons you want to share. Unknown GitHub versions and unmanaged addons appear in the list. You can also select a known GitHub group to share its custom edits instead of the stock GitHub package. All installed folders associated with one source are selected together. **Select All** selects every listed group; **Clear Selection** clears the checkboxes.
5. Click **Review Publication**. Custom entries show **Upload custom files**. GitHub entries show their recorded version. Confirm that the included and excluded lists are what you want.
6. Click **Publish Profile** and wait for upload verification.

The app remembers your chosen folders for that WoW installation after a successful publication. You can change the choices at each publish. Publishing reads your installed files without changing them or assigning ownership to unmanaged addons on the publisher.

The ZIP includes the selected addon folders and their contents, including custom Lua, textures, configuration files inside those folders, and nested libraries. The app excludes its ownership marker. WoW's `WTF` directory and SavedVariables outside AddOns stay local.

## On your follower computer

1. Close WoW.
2. Click **Sync from Profile**.
3. Review the custom entries and affected folders, then click **Sync Now**.

The app downloads custom ZIPs through rclone and checks the archive and content hashes before installation. Installed custom addons appear as **Installed - Custom Sync**, with **Custom files (rclone)** as their source. Use **Sync from Profile** for updates; GitHub Check/Install/Update buttons do not apply to these entries.

Existing same-name unmanaged folders or folders owned by another source block installation. If a folder blocks your first custom sync, back it up and move it out of AddOns yourself, then retry. Switching a previously GitHub-synced addon to a custom snapshot also has a different source identity: back up/move its existing follower folder and use **Stop Managing** for the old association before retrying. The app does not replace an unrelated folder automatically.

After installation, the app tracks ownership of the received files. If you edit them on the follower, further replacement or removal stops to protect your edits. Keep the authoritative custom edits on the publisher, publish again, and then sync the follower.

## Changes and removal

After editing a selected addon on the publisher, publish again. The follower will offer to replace its previous custom version. If the files match, the preview shows **Current** and no ZIP download is needed.

Unchecking an addon, disabling custom uploads, or deleting its publisher folder removes it from the next profile. Followers keep extra managed groups by default. To remove extras, explicitly select **Also uninstall managed sources absent from this profile** in the sync preview. Removal is allowed only for unchanged, owned folders, and happens after all requested installations succeed. Unrelated unmanaged folders stay untouched.

**Stop Managing** keeps a follower's installed files but removes its sync ownership association. **Remove Addon…** explicitly uninstalls a custom group with the usual ownership checks and confirmation.

## What appears in Dropbox or Google Drive

With a profile path like `dropbox:WoW/legacy-addons.json`, the storage contains:

```text
WoW/
  legacy-addons.json
  legacy-addons.json.packages/
    <archive-checksum>.zip
```

The JSON contains custom group names, folder names, stable source identities, and checksums. Custom ZIPs are uploaded and downloaded for verification before the new profile is committed. If a package upload fails, the previous profile remains available. Completed package uploads can remain after a later failure; retry publication to finish.

Old ZIPs are retained because the app does not delete cloud files automatically. Keep the ZIPs referenced by the current profile; deleting them prevents custom sync. Unchanged addon files produce the same archive checksum and filename.

Each ZIP supports up to **512 MB compressed**, **2 GB expanded**, and **100,000 archive entries**. Unsafe paths, links/junctions, invalid profiles, damaged or missing packages, folder conflicts, and local edits stop synchronization before that group's installed files change. The app applies one source group at a time; earlier completed groups remain if a later group fails.

Existing GitHub-only profiles remain supported. Profiles containing custom files use schema 2, which older app versions cannot read.
