using System.Security.Cryptography;
using System.Text;

namespace LegacyAddonManager.Services;

public static class TokenProtector
{
    public static string Protect(string value) => string.IsNullOrWhiteSpace(value) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    public static string ProtectExact(string value) => string.IsNullOrEmpty(value) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
}
