using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using LegacyAddonManager.Core;

namespace LegacyAddonManager.Services;

public sealed class BundledRclone(string settingsDirectory)
{
    public const string Version = "v1.75.1";
    public const string ArchiveHash = "200EB602C126D82AA38B51E0F6B9AE837473FF99B51278D3F6F837574C494D6E";
    public const string ExecutableHash = "033EEE51C9AD47C2DE2624B6674D355274BCD6CF0027A5F85DB4437BA24AE81C";
    private static readonly SemaphoreSlim extractionLock = new(1, 1);

    public async Task<string> EnsureAvailableAsync(CancellationToken ct)
    {
        await extractionLock.WaitAsync(ct);
        try { return await Task.Run(() => Extract(ct), ct); }
        finally { extractionLock.Release(); }
    }
    private string Extract(CancellationToken ct)
    {
        var root = Path.GetFullPath(Path.Combine(settingsDirectory, "Tools", "rclone-" + Version));
        SafeFiles.EnsureNoLinks(root);
        Directory.CreateDirectory(root);
        var executable = SafeFiles.Child(root, "rclone.exe");
        SafeFiles.EnsureNoLinks(executable);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(executable))
        {
            using var existing = File.OpenRead(executable);
            if (Convert.ToHexString(SHA256.HashData(existing)) == ExecutableHash) return executable;
        }
        using var archiveStream = typeof(BundledRclone).Assembly.GetManifestResourceStream("LegacyAddonManager.Tools.rclone.zip")
            ?? throw new IOException("The bundled rclone archive is missing. Rebuild with scripts/GetRclone.ps1 first.");
        if (Convert.ToHexString(SHA256.HashData(archiveStream)) != ArchiveHash) throw new InvalidDataException("The bundled rclone archive failed checksum verification.");
        archiveStream.Position = 0;
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("rclone-" + Version + "-windows-amd64/rclone.exe") ?? throw new InvalidDataException("The bundled rclone executable is missing.");
        var temporary = SafeFiles.Child(root, ".lam-rclone-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            using (var input = File.OpenRead(temporary))
                if (Convert.ToHexString(SHA256.HashData(input)) != ExecutableHash) throw new InvalidDataException("The extracted rclone executable failed checksum verification.");
            ct.ThrowIfCancellationRequested();
            SafeFiles.EnsureNoLinks(executable);
            File.Move(temporary, executable, true);
            var licensePath = SafeFiles.Child(root, "RCLONE-LICENSE.txt");
            SafeFiles.EnsureNoLinks(licensePath);
            using var license = typeof(BundledRclone).Assembly.GetManifestResourceStream("LegacyAddonManager.Tools.rclone-license.txt")!;
            using var destination = File.Create(licensePath);
            license.CopyTo(destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return executable;
    }
}
