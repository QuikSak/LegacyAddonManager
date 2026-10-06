using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

public sealed record AddonFolder(string SourcePath, string Name);

public static class AddonArchive
{
    public static void Extract(string zipPath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 100_000) throw new InvalidDataException("Archive has too many entries.");
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var parts = entry.FullName.Replace('\\', '/').TrimEnd('/').Split('/');
            foreach (var part in parts) SafeFiles.ValidateName(part);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive contains a symbolic link.");
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes the extraction directory.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive exceeds the 2 GB extraction limit.");
            if (!files.Add(path)) throw new InvalidDataException("Archive contains duplicate Windows file paths.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                written += read;
                if (written > entry.Length) throw new InvalidDataException("Archive entry exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
        }
    }

    public static IReadOnlyList<AddonFolder> Discover(string directory)
    {
        var found = new List<AddonFolder>();
        Walk(directory);
        if (found.Count == 0) throw new InvalidDataException("No addon folders containing .toc files were found in this ZIP.");
        if (found.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != found.Count)
            throw new InvalidDataException("Multiple addon folders have the same destination name. Choose a packaged release ZIP instead.");
        return found;

        void Walk(string current)
        {
            SafeFiles.EnsureNoLinks(current);
            var tocs = Directory.EnumerateFiles(current).Where(f => Path.GetExtension(f).Equals(".toc", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (tocs.Length > 0)
            {
                var folderName = Path.GetFileName(current);
                var names = tocs.Select(f => Regex.Replace(Path.GetFileNameWithoutExtension(f), @"_(Mainline|Classic|Vanilla|BCC|TBC|Wrath|Cata|Mists)$", "", RegexOptions.IgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var name = names.FirstOrDefault(n => n.Equals(folderName, StringComparison.OrdinalIgnoreCase)) ??
                    (names.Length == 1 ? names[0] : throw new InvalidDataException($"Ambiguous .toc names in {folderName}. Choose a packaged addon ZIP."));
                SafeFiles.ValidateName(name);
                if (name.StartsWith(".lam-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved manager folder name.");
                found.Add(new(current, name));
                return; // Nested .toc libraries remain inside their parent addon.
            }
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (name is ".git" or ".github" or "__MACOSX") continue;
                Walk(child);
            }
        }
    }
}
