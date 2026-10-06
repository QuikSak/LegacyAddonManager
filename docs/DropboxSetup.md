# Dropbox setup for Legacy Addon Manager

Use one computer as the **publisher**, where you manage addon versions, and the other as the **follower**, which installs those published versions.

The app includes rclone and transfers an addon profile and any selected custom ZIPs directly to Dropbox. GitHub-version packages come from GitHub; custom packages come from Dropbox through rclone. You do not need to install the Dropbox desktop app or a separate copy of rclone.

For normal Dropbox setup, leave rclone's client ID and secret blank. Custom credentials are optional. [Official rclone Dropbox instructions](https://rclone.org/dropbox/#configuration).

To upload custom, unmanaged, or unknown-version addons, see the [custom addon upload guide](CustomAddonSync.md). Use app 1.4.0 or newer on both computers.

## Before you start

- Have a Dropbox account and use the same account on both computers for this guide.
- Open Legacy Addon Manager on each computer and select its own WoW installation with **Browse**.
- Close WoW before installing or syncing addons.
- For stock GitHub sync, install addons through the manager. For manual addons, assign their source using **Edit Source**, then use **Update Selected** to establish a recorded version. Associate all folders from a multi-addon repository first and review the replacement confirmation. To keep and share custom or unknown-version files, select them in publish options instead; no GitHub installation is required.

## 1. Connect the publisher computer

1. Click **rclone Setup…**, then **Configure Remote…**. A console wizard opens.
2. Enter the following choices. Wording and prompt order can vary; some prompts may be omitted.

| Wizard item | Enter or choose |
| --- | --- |
| Create a remote | `n` |
| Remote name | `dropbox` |
| Storage type | `dropbox` (type the name rather than a menu number) |
| Client ID / App key | Press Enter to leave blank |
| Client secret / App secret | Press Enter to leave blank |
| Advanced settings, if asked | `n` |
| Browser authentication, if asked | `y` |
| Keep the remote | `y` |
| Exit the wizard | `q` |

3. When the browser opens, sign in to your Dropbox account and approve rclone's requested access. Wait for the console to confirm authentication, then save and quit as above. If your wizard asks for an authorization code, follow its displayed browser instructions and enter the code there. [Rclone's Dropbox authorization reference](https://rclone.org/dropbox/#configuration).
4. Back in the app, click **Refresh Remotes** if necessary and select **dropbox:**.
5. In **Remote profile path**, enter:

   ```text
   dropbox:WoW/legacy-addons.json
   ```

6. Click **Test Connection**, then **Use rclone**. A message that this filename has no profile yet is expected on the first setup. Setup reads the remote; publication creates the profile.
7. The main window should show **Publisher**. Click **Publish to Profile**, choose any custom files to include, then click **Review Publication**. Review the included versions and excluded addons, then click **Publish Profile**.
8. Wait for the successful upload and verification message. On the Dropbox website, look for **WoW → legacy-addons.json**. Finish publishing before connecting the second computer.

The profile path is a remote path, not a Dropbox sharing link or a Windows folder path. Keep the colon after `dropbox` and use forward slashes.

Leave **Encrypted config password** blank unless you deliberately encrypted your rclone config. This field is not your Dropbox password.

## 2. Connect the follower computer

1. Open Legacy Addon Manager on the second computer and select its own WoW installation.
2. Click **rclone Setup… → Configure Remote…** and repeat the wizard choices above. Sign in to the same Dropbox account independently on this computer.
3. Select **dropbox:** and enter exactly `dropbox:WoW/legacy-addons.json`.
4. Click **Test Connection → Use rclone**. Because the profile was already published by the first computer, the main window should show **Follower**. Publishing is disabled here.
5. Click **Sync from Profile**, review the versions and affected folders, then click **Sync Now**.

Extra managed sources remain installed by default. To remove them and match the published managed-source list, explicitly select **Also uninstall managed sources absent from this profile** in the preview. Unmanaged addons are preserved.

## 3. Keep the computers synchronized

1. Install, update, or remove managed addons on the publisher.
2. Click **Publish to Profile** and confirm publication.
3. On the follower, close WoW, click **Sync from Profile**, review the preview, and click **Sync Now**.

The follower installs the published versions, including downgrades when needed. Transfers happen when you use these buttons. Keep one publisher for this profile.

The profile covers complete manager-installed GitHub groups and any custom groups explicitly selected for upload. WoW's `WTF` folder, SavedVariables outside AddOns, UI layouts, and unselected unmanaged addons need a separate backup or transfer.

Each PC keeps its own `settings.json` and publisher identity. Keep those settings separate. Dropbox credentials stay in the local rclone config, normally `%LocalAppData%\WoWAddonManager\rclone.conf`.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| No remote appears | Save the remote with `y`, quit with `q`, then click **Refresh Remotes**. Use the config file shown in the app. |
| The browser does not open | Keep the wizard open and open the authentication URL it displays in a browser on that PC. Follow the wizard's instructions. |
| Both PCs show Publisher | Finish publishing on the first PC, then reconnect the second through **rclone Setup…**. Check that both use the same Dropbox account and exact remote path. |
| Profile missing on the follower | Confirm the publisher's upload succeeded and `WoW/legacy-addons.json` exists in that account. |
| The connection worked previously but now fails | Open **Configure Remote…**, edit the Dropbox remote, and follow the wizard to authenticate again. Then test the connection. |
| An addon is excluded from publication | Read the reason. Establish its GitHub version with a manager installation, or select its installed custom files in publish options. Missing/pending folders must be installed first. |
| An existing folder blocks sync | Associate it with the correct GitHub source using **Edit Source**, or back it up and move it out of AddOns before retrying. |
| No changes in the preview | The follower already matches the published profile. Publish again after changing the publisher's addons. |

For a custom Dropbox client ID/secret, follow [rclone's optional app setup guide](https://rclone.org/dropbox/#get-your-own-dropbox-app-id). This guide assumes a personal Dropbox account; business team folders use different path rules described in [rclone's business-account documentation](https://rclone.org/dropbox/#dropbox-for-business).

Instructions checked against the app and official rclone documentation on October 5, 2026. Dropbox sign-in was not performed against your account.
