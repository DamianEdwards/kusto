using System.Security.Cryptography;
using System.Text;

namespace Kusto.Cli;

/// <summary>
/// Computes the deterministic, profile-specific name of the protected MSAL token
/// cache used by the WAM broker. The name is derived from the effective configuration
/// directory so distinct CLI profiles (distinct config paths) never share a cache, and
/// the same profile always resolves to the same cache. Never contains secrets.
/// </summary>
internal static class WamTokenCacheName
{
    private const string Prefix = "kusto-cli";

    public static string Resolve(string configDirectory)
    {
        var normalized = NormalizeDirectory(configDirectory);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var suffix = Convert.ToHexStringLower(hash)[..16];
        return $"{Prefix}-{suffix}";
    }

    private static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant();
    }
}
