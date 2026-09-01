using System.Reflection;
using NuGet.Versioning;

namespace Kusto.Cli;

internal static class VersionHelper
{
    public static bool TryParse(string? input, out NuGetVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        if (!NuGetVersion.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    public static bool IsDevBuild(NuGetVersion version)
        => version.IsPrerelease
           && version.ReleaseLabels.Any(label =>
               string.Equals(label, "dev", StringComparison.OrdinalIgnoreCase));

    public static bool IsPreReleaseBuild(NuGetVersion version)
        => version.IsPrerelease && !IsDevBuild(version);

    public static bool IsStableBuild(NuGetVersion version)
        => !version.IsPrerelease;

    public static bool IsUpdateCandidate(
        NuGetVersion current,
        NuGetVersion candidate,
        bool allowPreRelease,
        bool stableOnly = false)
    {
        if (candidate <= current)
        {
            return false;
        }

        if (stableOnly)
        {
            return IsStableBuild(candidate);
        }

        if (IsDevBuild(current))
        {
            return true;
        }

        if (IsPreReleaseBuild(current))
        {
            return !IsDevBuild(candidate);
        }

        return allowPreRelease
            ? !IsDevBuild(candidate)
            : IsStableBuild(candidate);
    }

    public static string? GetCurrentVersion()
    {
        var attribute = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attribute?.InformationalVersion is { } informationalVersion)
        {
            var metadataIndex = informationalVersion.IndexOf('+');
            return metadataIndex >= 0
                ? informationalVersion[..metadataIndex]
                : informationalVersion;
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
    }
}
