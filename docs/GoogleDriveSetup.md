# Google Drive setup for Legacy Addon Manager

Use this guide to keep the managed GitHub addons on two computers at the same versions. Choose one computer as the **publisher**; the other is the **follower**.

The app includes rclone. Google Drive stores an addon profile and any selected custom ZIPs; the follower downloads recorded GitHub versions from GitHub and custom files through rclone. Transfers happen when you click Publish or Sync.

To upload custom, unmanaged, or unknown-version addons, see the [custom addon upload guide](CustomAddonSync.md). Use app 1.4.0 or newer on both computers.

## Before you start

- Run Legacy Addon Manager on each computer and select that computer's WoW installation with **Browse**.
- Use the same Google account on both computers for this setup.
- Close WoW before installing or syncing addons.
- For stock GitHub sync, install addons through the manager. For manual addons, assign their GitHub source with **Edit Source**, then use **Update Selected** to establish a recorded version. Associate all folders from a multi-addon repository first and review the replacement confirmation. To keep and share custom or unknown-version files, select them in publish options instead; no GitHub installation is required.

## 1. Create your Google connection credentials once

Rclone's shared Google Drive client ID is being retired during 2026. Create your own instead of leaving the client ID blank. [Rclone's current Google Drive guidance](https://rclone.org/drive/#making-your-own-client-id).

1. Open the [Google Cloud Console](https://console.cloud.google.com/) and sign in.
2. Use the project selector at the top to create a project named `Legacy Addon Manager Sync`. Select that project.
3. Go to **APIs & Services → Library**, find **Google Drive API**, and click **Enable**. [Google's API setup instructions](https://developers.google.com/drive/api/quickstart/java#enable_the_api).
4. Open **Google Auth platform → Branding**. If prompted, click **Get Started**. Enter an app name such as `My WoW Addon Sync`, your support email, and your contact email. Choose **External** for a personal Gmail account and finish setup.
5. Under **Audience → Test users**, add the Google account you will use on both computers.
6. Under **Data Access**, use **Add or remove scopes** to add `https://www.googleapis.com/auth/drive` and save. This matches the full Drive permission selected below. [Google's consent-screen instructions](https://developers.google.com/workspace/guides/configure-oauth-consent).
7. Go to **Google Auth platform → Clients → Create Client**. Select **Desktop app**, name it `WoW rclone`, and create it. Save the **Client ID** and **Client Secret** locally for the next step. Use this same pair on both computers. [Google's desktop credential instructions](https://developers.google.com/workspace/guides/create-credentials#desktop-app).
8. Under **Audience**, choose **Publish App** to move to **In production** when Google permits it. This publishes the OAuth configuration; the addon profile is still private to your Drive account. If Google requests website or privacy-policy information, complete the requested Branding fields. You can start in Testing, but Drive authorization expires after seven days and must be renewed. [Google's publishing-status explanation](https://support.google.com/cloud/answer/15549945?hl=en).

A personal OAuth app can be used without verification, but Google may show an unverified-app warning at sign-in. If it identifies the app you just created, use **Advanced** and the option to continue to your app, then approve the requested permissions. [Google's personal-use exception](https://support.google.com/cloud/answer/13464323?hl=en).

## 2. Connect the publisher computer

1. In Legacy Addon Manager, click **rclone Setup…**, then **Configure Remote…**.
2. In the console wizard, enter these choices. Some prompts may be omitted or appear in a different order.

| Wizard item | Enter or choose |
| --- | --- |
| New remote | `n` |
| Remote name | `gdrive` |
| Storage type | `drive` (Google Drive) |
| Client ID / secret | Your saved pair |
| Scope | `1` / `drive` (full Drive access) |
| Service-account file | Leave empty |
| Root folder, if asked | Leave empty |
| Advanced settings, if asked | `n` |
| Browser authentication | `y` |
| Shared / Team Drive | `n` for personal My Drive |
| Keep the remote | `y` |
| Exit the wizard | `q` |

Sign in with your chosen Google account when the browser opens and approve Drive access. [Official rclone wizard reference](https://rclone.org/drive/#configuration).

3. Back in the app, click **Refresh Remotes** if necessary and select **gdrive:**.
4. Enter this in **Remote profile path**:

   ```text
   gdrive:WoW/legacy-addons.json
   ```

5. Click **Test Connection**, then **Use rclone**. For the first setup, a missing profile is expected: setup connects this computer as the publisher without creating the remote file.
6. In the main window, click **Publish to Profile**. Choose any custom files to include, click **Review Publication**, review the included and excluded items, then click **Publish Profile**.
7. Wait for the successful upload and verification message. In Google Drive, look for **My Drive → WoW → legacy-addons.json**. Finish this publication before connecting the follower.

Leave the app's **Encrypted config password** option blank unless you deliberately encrypted your rclone config. It is not your Google account password.

## 3. Connect the follower computer

1. Open Legacy Addon Manager on the second computer and select its WoW installation.
2. Click **rclone Setup… → Configure Remote…**. Repeat the wizard choices above using the same Client ID and Client Secret. Sign in to the same Google account independently on this computer.
3. Select **gdrive:** and enter exactly `gdrive:WoW/legacy-addons.json`.
4. Click **Test Connection → Use rclone**. The main window should show **Follower**. Publishing is disabled on this computer.
5. Click **Sync from Profile**, review the versions and affected folders, then click **Sync Now**.

To also remove extra managed sources from the follower, explicitly select **Also uninstall managed sources absent from this profile** in the preview. Extra sources are kept by default. Unmanaged addons are preserved.

## 4. Keep the computers synchronized

1. On the publisher, install, update, or remove managed addons as needed.
2. Click **Publish to Profile** and confirm publication.
3. On the follower, close WoW and click **Sync from Profile**, then **Sync Now**.

The follower uses the published versions, which can include downgrades. This feature synchronizes manager-installed GitHub groups and any custom groups selected for upload. WoW's `WTF` folder, SavedVariables outside AddOns, UI layouts, and unselected unmanaged addons require a separate backup or transfer.

Keep each computer's local `settings.json` separate. Its publisher identity and ownership records belong to that computer. Credentials remain in the local rclone config, normally `%LocalAppData%\WoWAddonManager\rclone.conf`.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| No remote in the dropdown | Save with `y`, quit with `q`, then click **Refresh Remotes**. Ensure you used the config shown in the app. |
| Both computers show Publisher | Publish successfully on the first computer, then reconnect the second through **rclone Setup…**. Check the Google account and remote path on both PCs. |
| Profile missing on the follower | Finish the publisher's upload first. Confirm the same account and exact `WoW/legacy-addons.json` path. |
| Connection stops working after a week | Check the Google OAuth publishing status. Testing authorizations expire after seven days. Move to In production if possible, then edit the remote in **Configure Remote…** and authenticate again. |
| Google rejects authorization | Check the Client ID/secret, enabled Drive API, and your account in Test users if still Testing. A work or school account may also need administrator approval. |
| An addon is excluded from publication | Review the reason. Establish its GitHub version with a manager installation, or select its installed custom files in publish options. Missing/pending folders must be installed first. |
| An existing addon folder blocks sync | Associate it with the correct GitHub source using **Edit Source**, or back it up and move it out of AddOns before retrying. |
| No changes in the preview | The follower already matches the published profile. Publish again after changing the publisher's addons. |

Google can change its setup screens. The linked official documentation is the reference if a label differs. These instructions were checked on October 5, 2026; cloud sign-in was not performed against your account.
