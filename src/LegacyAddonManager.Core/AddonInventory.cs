using System.Text;
using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

public sealed class TocMetadata
{
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Author { get; init; } = "";
    public string Interface { get; init; } = "";
    public string SavedVariables { get; init; } = "";
    public string SavedVariablesPerCharacter { get; init; } = "";
    public string Dependencies { get; init; } = "";
    public string OptionalDependencies { get; init; } = "";
}

public static class TocReader
{
    public static TocMetadata Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart().TrimStart('\uFEFF');
            if (!trimmed.StartsWith("##", StringComparison.Ordinal)) continue;
            var colon = trimmed.IndexOf(':', 2);
            if (colon < 0) continue;
            fields[trimmed[2..colon].Trim()] = trimmed[(colon + 1)..].Trim();
        }
        string Field(params string[] names) => names.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        string Plain(string value) => Regex.Replace(Regex.Replace(value, @"\|c[0-9a-fA-F]{8}|\|r", ""), @"\|T[^\r\n]*?\|t", "").Replace("||", "|").Trim();
        return new()
        {
            Title = Plain(Field("Title", "Title-enUS", "Title-enGB")), Version = Field("Version"), Notes = Plain(Field("Notes", "Notes-enUS", "Notes-enGB")),
            Author = Plain(Field("Author")), Interface = Field("Interface"), SavedVariables = Field("SavedVariables"),
            SavedVariablesPerCharacter = Field("SavedVariablesPerCharacter"), Dependencies = Field("Dependencies", "RequiredDeps", "Dep", "Deps"),
            OptionalDependencies = Field("OptionalDependencies", "OptionalDeps")
        };
    }

    public static (TocMetadata Metadata, string TocFile) ReadFolder(string directory)
    {
        SafeFiles.EnsureNoLinks(directory);
        var name = Path.GetFileName(directory);
        var tocs = Directory.EnumerateFiles(directory).Where(f => Path.GetExtension(f).Equals(".toc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f).Equals(name + ".toc", StringComparison.OrdinalIgnoreCase) ? 0 :
                Path.GetFileName(f).Equals(name + "_Wrath.toc", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (tocs.Length == 0) throw new InvalidDataException("The addon folder contains no .toc file.");
        var toc = tocs[0];
        SafeFiles.EnsureNoLinks(toc);
        if (new FileInfo(toc).Length > 1024 * 1024) throw new InvalidDataException("The .toc file exceeds the 1 MB metadata limit.");
        return (Parse(File.ReadAllText(toc, Encoding.UTF8)), Path.GetFileName(toc));
    }
}

public sealed record DiscoveredAddon(string FolderName, string FolderPath, TocMetadata Metadata, string TocFile, string? Error = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.Title) ? FolderName : Metadata.Title;
}
public sealed record InventoryEntry(string? FolderName, DiscoveredAddon? Local, ManagedAddon? Management)
{
    public bool IsInstalled => Local != null;
    public string DisplayName => Local?.DisplayName ?? (FolderName != null ? Management?.FolderDisplayNames.GetValueOrDefault(FolderName) ?? FolderName : Management?.Name) ?? "Addon";
}
public sealed record InventorySnapshot(IReadOnlyList<InventoryEntry> Entries, IReadOnlyList<string> Warnings);

public static class ManagementPaths
{
    public static string? ForWoW(string wowPath) => string.IsNullOrWhiteSpace(wowPath) ? null :
        Path.GetFullPath(Path.Combine(wowPath, "Interface", "AddOns")).TrimEnd(Path.DirectorySeparatorChar);
    public static bool Same(string? left, string? right) => string.Equals(left?.TrimEnd(Path.DirectorySeparatorChar), right?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    public static bool AppliesTo(ManagedAddon addon, string wowPath) => addon.AddOnsPath == null || Same(addon.AddOnsPath, ForWoW(wowPath));
}

public sealed class AddonInventory
{
    public InventorySnapshot Read(AppSettings settings, CancellationToken ct = default)
    {
        var discovered = new List<DiscoveredAddon>();
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.WoWPath))
        {
            var root = AddonInstaller.GetAddOnsPath(settings.WoWPath);
            if (Directory.Exists(root))
            {
                foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(folder);
                    if (name.StartsWith(".lam-transaction-", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (!Directory.EnumerateFiles(folder).Any(f => Path.GetExtension(f).Equals(".toc", StringComparison.OrdinalIgnoreCase))) continue;
                        var (metadata, toc) = TocReader.ReadFolder(folder);
                        discovered.Add(new(name, folder, metadata, toc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"{name}: {ex.Message}");
                        discovered.Add(new(name, folder, new(), "", ex.Message));
                    }
                }
            }
        }
        var records = settings.Addons.Where(a => ManagementPaths.AppliesTo(a, settings.WoWPath)).ToArray();
        var result = discovered.Select(local => new InventoryEntry(local.FolderName, local,
            records.FirstOrDefault(a => a.InstalledFolders.Contains(local.FolderName, StringComparer.OrdinalIgnoreCase)))).ToList();
        foreach (var record in records)
        {
            if (record.InstalledFolders.Count == 0) result.Add(new(null, null, record));
            foreach (var missing in record.InstalledFolders.Where(f => !discovered.Any(d => d.FolderName.Equals(f, StringComparison.OrdinalIgnoreCase))))
                result.Add(new(missing, null, record));
        }
        return new(result.OrderBy(e => e.Management?.RepositoryUrl ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }
}
