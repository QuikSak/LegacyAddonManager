using System.Numerics;
using System.Text.RegularExpressions;

namespace LegacyAddonManager.Core;

public enum VersionComparison { Current, Different, Unknown }

public static class AddonVersionComparer
{
    public static VersionComparison Compare(ManagedAddon addon, string? folderName, string? tocVersion, RepositoryVersion latest)
    {
        var adopted = addon.AdoptedFolders.Any(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        if (!adopted && addon.InstalledVersion != null)
            return addon.InstalledVersion == latest.Version && addon.InstalledVersionKind == latest.Kind ? VersionComparison.Current : VersionComparison.Different;
        if (latest.Kind != "release" || string.IsNullOrWhiteSpace(tocVersion)) return VersionComparison.Unknown;
        var local = tocVersion.Trim();
        var remote = latest.Version.Trim();
        if (IsPlaceholder(local) || IsPlaceholder(remote)) return VersionComparison.Unknown;
        var a = Normalize(local);
        var b = Normalize(remote);
        if (a != null && b != null) return a == b ? VersionComparison.Current : VersionComparison.Different;
        return local.Equals(remote, StringComparison.OrdinalIgnoreCase) ? VersionComparison.Current : VersionComparison.Unknown;
    }

    private static bool IsPlaceholder(string value) => value.IndexOfAny(['@', '$', '{', '}']) >= 0 ||
        Regex.IsMatch(value, @"^(unknown|dev|development|unreleased|git|working|n/?a)$", RegexOptions.IgnoreCase);

    private static string? Normalize(string value)
    {
        if (value.Length > 128) return null;
        var match = Regex.Match(value, @"^[vV]?(?<core>\d+(?:\.\d+){0,3})(?<suffix>-[A-Za-z0-9][A-Za-z0-9.-]*)?(?:\+[A-Za-z0-9.-]+)?$");
        if (!match.Success) return null;
        var segments = match.Groups["core"].Value.Split('.').Select(BigInteger.Parse).ToList();
        while (segments.Count < 4) segments.Add(BigInteger.Zero);
        return string.Join('.', segments) + match.Groups["suffix"].Value.ToLowerInvariant();
    }
}
