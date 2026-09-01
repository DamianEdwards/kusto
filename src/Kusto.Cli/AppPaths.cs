namespace Kusto.Cli;

internal static class AppPaths
{
    public static string GetAppHomeDirectory()
    {
        var configuredHome = Environment.GetEnvironmentVariable(AppIdentity.HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Path.GetFullPath(ExpandUserProfile(configuredHome));
        }

        var configuredConfig = Environment.GetEnvironmentVariable("KUSTO_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredConfig))
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(ExpandUserProfile(configuredConfig)));
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                return configDirectory;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kusto");
    }

    public static string GetUpdateStatePath()
        => Path.Combine(GetAppHomeDirectory(), "update-state.json");

    public static string GetUpdateLockPath()
        => Path.Combine(GetAppHomeDirectory(), ".update-lock");

    private static string ExpandUserProfile(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            path[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
