using System.Security.Cryptography;

namespace LegacyAddonManager.Core;

public static class SafeFiles
{
    public static string Child(string parent, string name)
    {
        ValidateName(name);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var child = Path.GetFullPath(Path.Combine(root, name));
        if (!child.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes the intended directory.");
        return child;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\') || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException($"Unsafe addon or archive folder name: {name}");
        var stem = name.Split('.')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("Reserved Windows file name in archive.");
    }

    public static void EnsureNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Symbolic links and junctions are not supported: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    public static Dictionary<string, string> HashFiles(string directory, string excludedName, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(directory);
        return result;
        void Walk(string current)
        {
            ct.ThrowIfCancellationRequested();
            EnsureNoLinks(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                ct.ThrowIfCancellationRequested();
                EnsureNoLinks(file);
                var relative = Path.GetRelativePath(directory, file);
                if (relative.Equals(excludedName, StringComparison.OrdinalIgnoreCase)) continue;
                using var stream = File.OpenRead(file);
                result.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
            }
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                result.Add(Path.GetRelativePath(directory, child) + Path.DirectorySeparatorChar, "<directory>");
                Walk(child);
            }
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        EnsureNoLinks(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            EnsureNoLinks(file);
            File.Copy(file, Child(destination, Path.GetFileName(file)));
        }
        foreach (var child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Child(destination, Path.GetFileName(child)));
    }

    public static void DeleteOwnedWorkDirectory(string directory, string parent)
    {
        var absolute = Path.GetFullPath(directory);
        var expected = Child(parent, Path.GetFileName(absolute));
        if (!absolute.Equals(expected, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith(".lam-", StringComparison.Ordinal))
            throw new IOException("Refusing to remove an unexpected working directory.");
        EnsureNoLinks(absolute);
        if (Directory.Exists(absolute))
        {
            ValidateTree(absolute);
            Directory.Delete(absolute, true);
        }
    }

    private static void ValidateTree(string directory)
    {
        EnsureNoLinks(directory);
        foreach (var file in Directory.EnumerateFiles(directory)) EnsureNoLinks(file);
        foreach (var child in Directory.EnumerateDirectories(directory)) ValidateTree(child);
    }
}
