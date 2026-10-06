using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LegacyAddonManager.Core;

public sealed record LocalPublishCandidate(string Name, IReadOnlyList<string> Folders, string SourceId, string Description);

public static class CustomAddonPackages
{
    public const long MaxZipBytes = 512L * 1024 * 1024;
    public const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;

    public static IReadOnlyList<LocalPublishCandidate> Candidates(AppSettings settings, CancellationToken ct = default)
    {
        var entries = new AddonInventory().Read(settings, ct).Entries.Where(e => e.Local != null).ToArray();
        var candidates = new List<LocalPublishCandidate>();
        foreach (var record in settings.Addons.Where(a => ManagementPaths.AppliesTo(a, settings.WoWPath)))
        {
            var folders = entries.Where(e => e.Management?.Id == record.Id).Select(e => e.FolderName!).ToArray();
            if (folders.Length == 0) continue;
            candidates.Add(new(record.Name, folders, Identity("source:" + record.Id),
                record.IsCustom ? "Custom addon files" : record.InstalledVersion == null || record.AdoptedFolders.Count > 0
                    ? "GitHub version unknown — upload installed files" : "Use installed files instead of the GitHub package (for custom edits)"));
        }
        foreach (var entry in entries.Where(e => e.Management == null))
            candidates.Add(new(entry.DisplayName, new[] { entry.FolderName! }, Identity("folder:" + entry.FolderName!.ToUpperInvariant()), "Unmanaged — upload installed files"));
        return candidates.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static (SyncProfileAddon Addon, string ZipPath) Create(string addonRoot, LocalPublishCandidate candidate, string work, CancellationToken ct)
    {
        var folders = candidate.Folders.Select(f => new AddonFolder(SafeFiles.Child(addonRoot, f), f)).ToArray();
        foreach (var folder in folders) _ = TocReader.ReadFolder(folder.SourcePath);
        var contentHash = AddonContent.Hash(folders, ct);
        var temporaryZip = SafeFiles.Child(work, candidate.SourceId + ".zip");
        long total = 0;
        var count = 0;
        using (var zip = ZipFile.Open(temporaryZip, ZipArchiveMode.Create))
        {
            foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                zip.CreateEntry(folder.Name + "/").LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); count++;
                var files = SafeFiles.HashFiles(folder.SourcePath, AddonInstaller.MarkerFile, ct);
                foreach (var relative in files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > 100_000) throw new InvalidDataException("Custom addon package has too many files or directories.");
                    var entryName = folder.Name + "/" + relative.Replace('\\', '/');
                    foreach (var part in entryName.TrimEnd('/').Split('/')) SafeFiles.ValidateName(part);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    if (relative.EndsWith(Path.DirectorySeparatorChar)) continue;
                    var source = Path.GetFullPath(Path.Combine(folder.SourcePath, relative));
                    SafeFiles.EnsureNoLinks(source);
                    using var input = File.OpenRead(source);
                    total = checked(total + input.Length);
                    if (total > MaxExpandedBytes) throw new InvalidDataException("Custom addon package exceeds the 2 GB expanded limit.");
                    using var output = entry.Open();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                }
            }
        }
        if (new FileInfo(temporaryZip).Length > MaxZipBytes) throw new InvalidDataException("Custom addon ZIP exceeds the 512 MB limit.");
        var archiveHash = HashZip(temporaryZip);
        var target = new SyncProfileAddon
        {
            Name = candidate.Name.Length > 256 ? candidate.Name[..256] : candidate.Name, CustomSourceId = candidate.SourceId,
            VersionKind = "custom", Version = contentHash, ContentHash = contentHash, ArchiveHash = archiveHash, Folders = candidate.Folders.ToList()
        };
        // Validate the actual ZIP, not just the source tree; files may change while packaging.
        var extracted = SafeFiles.Child(work, ".lam-verify-" + Guid.NewGuid().ToString("N"));
        try { _ = ExtractVerified(temporaryZip, extracted, target, ct); }
        finally { SafeFiles.DeleteOwnedWorkDirectory(extracted, work); }
        if (AddonContent.Hash(folders, ct) != contentHash) throw new IOException($"{candidate.Name} changed during packaging. Close WoW and retry publishing.");
        var finalZip = SafeFiles.Child(work, archiveHash + ".zip");
        File.Move(temporaryZip, finalZip);
        return (target, finalZip);
    }

    public static IReadOnlyList<AddonFolder> ExtractVerified(string zipPath, string destination, SyncProfileAddon target, CancellationToken ct)
    {
        SyncProfileStore.ValidateAddon(target);
        if (!target.IsCustom || new FileInfo(zipPath).Length > MaxZipBytes || HashZip(zipPath) != target.ArchiveHash)
            throw new InvalidDataException("Custom addon ZIP checksum or size does not match the profile. Installed files were preserved.");
        AddonArchive.Extract(zipPath, destination, ct);
        if (Directory.EnumerateFiles(destination).Any() ||
            !AddonContent.FoldersEqual(Directory.EnumerateDirectories(destination).Select(Path.GetFileName)!, target.Folders))
            throw new InvalidDataException("Custom addon ZIP contains unexpected root folders or files.");
        var folders = target.Folders.Select(f => new AddonFolder(SafeFiles.Child(destination, f), f)).ToArray();
        foreach (var folder in folders)
        {
            _ = TocReader.ReadFolder(folder.SourcePath);
            if (File.Exists(Path.Combine(folder.SourcePath, AddonInstaller.MarkerFile))) throw new InvalidDataException("Custom addon ZIP contains an ownership file.");
        }
        if (AddonContent.Hash(folders, ct) != target.ContentHash) throw new InvalidDataException("Custom addon contents do not match the published profile.");
        return folders;
    }

    public static string HashZip(string path)
    {
        SafeFiles.EnsureNoLinks(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static string Identity(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
