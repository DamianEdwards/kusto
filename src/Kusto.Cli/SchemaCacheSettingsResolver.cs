using System.Runtime.InteropServices;

namespace Kusto.Cli;

public sealed class SchemaCacheSettingsResolver(
    Func<string, string?>? getEnvironmentVariable = null,
    Func<Environment.SpecialFolder, string>? getFolderPath = null,
    Func<string>? getUserHomeDirectory = null,
    Func<OSPlatform, bool>? isOSPlatform = null)
{
    public const int DefaultTtlSeconds = 24 * 60 * 60;
    public const string CacheEnabledEnvironmentVariable = "KUSTO_SCHEMA_CACHE_ENABLED";
    public const string CachePathEnvironmentVariable = "KUSTO_SCHEMA_CACHE_PATH";
    public const string CacheTtlEnvironmentVariable = "KUSTO_SCHEMA_CACHE_TTL_SECONDS";

    private readonly Func<string, string?> _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    private readonly Func<Environment.SpecialFolder, string> _getFolderPath = getFolderPath ?? Environment.GetFolderPath;
    private readonly Func<string> _getUserHomeDirectory = getUserHomeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private readonly Func<OSPlatform, bool> _isOSPlatform = isOSPlatform ?? RuntimeInformation.IsOSPlatform;

    public ResolvedSchemaCacheSettings Resolve(KustoConfig config, string clusterUrl, string database)
    {
        ArgumentNullException.ThrowIfNull(config);

        var cacheConfig = config.SchemaCache ?? new SchemaCacheConfig();
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var enabled = ResolveEnabled(cacheConfig.Enabled);
        if (!enabled)
        {
            return new ResolvedSchemaCacheSettings(false, string.Empty, TimeSpan.Zero);
        }

        var ttlSeconds = ResolveTtlSeconds(cacheConfig, normalizedClusterUrl, database);
        var cacheDirectory = ResolveCacheDirectory(cacheConfig);

        return new ResolvedSchemaCacheSettings(true, cacheDirectory, TimeSpan.FromSeconds(ttlSeconds));
    }

    private bool ResolveEnabled(bool configuredEnabled)
    {
        var value = _getEnvironmentVariable(CacheEnabledEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return configuredEnabled;
        }

        return ParseBoolean(CacheEnabledEnvironmentVariable, value);
    }

    private int ResolveTtlSeconds(SchemaCacheConfig cacheConfig, string clusterUrl, string database)
    {
        var value = _getEnvironmentVariable(CacheTtlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ParsePositiveInt(CacheTtlEnvironmentVariable, value);
        }

        var matchedOverride = FindMatchingOverride(cacheConfig, clusterUrl, database);
        var ttlSeconds = matchedOverride?.TtlSeconds ?? cacheConfig.TtlSeconds;
        if (ttlSeconds <= 0)
        {
            throw new UserFacingException("Schema cache TTL must be a positive number of seconds.");
        }

        return ttlSeconds;
    }

    private string ResolveCacheDirectory(SchemaCacheConfig cacheConfig)
    {
        var configuredPath = _getEnvironmentVariable(CachePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = cacheConfig.Path;
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = ResolveDefaultCacheDirectory();
        }

        try
        {
            return Path.GetFullPath(configuredPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UserFacingException($"'{configuredPath}' is not a valid schema cache path.", ex);
        }
    }

    private string ResolveDefaultCacheDirectory()
    {
        if (_isOSPlatform(OSPlatform.Windows))
        {
            var localApplicationData = _getFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApplicationData))
            {
                return Path.Combine(localApplicationData, "kusto", "schema-cache");
            }
        }

        var userHomeDirectory = _getUserHomeDirectory();
        if (string.IsNullOrWhiteSpace(userHomeDirectory))
        {
            throw new UserFacingException("Unable to determine the user home directory for the schema cache.");
        }

        if (_isOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(userHomeDirectory, "Library", "Caches", "kusto", "schema-cache");
        }

        var xdgCacheHome = _getEnvironmentVariable("XDG_CACHE_HOME");
        return string.IsNullOrWhiteSpace(xdgCacheHome)
            ? Path.Combine(userHomeDirectory, ".cache", "kusto", "schema-cache")
            : Path.Combine(xdgCacheHome, "kusto", "schema-cache");
    }

    private static SchemaCacheOverride? FindMatchingOverride(SchemaCacheConfig cacheConfig, string clusterUrl, string database)
    {
        foreach (var current in cacheConfig.Overrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(current.ClusterUrl) ||
                string.IsNullOrWhiteSpace(current.Database))
            {
                continue;
            }

            var normalizedOverrideClusterUrl = ClusterUtilities.NormalizeClusterUrl(current.ClusterUrl);
            if (string.Equals(normalizedOverrideClusterUrl, clusterUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Database, database, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }

        return null;
    }

    private static bool ParseBoolean(string name, string value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => throw new UserFacingException($"'{value}' is not a valid value for {name}. Use true, false, 1, or 0.")
        };
    }

    private static int ParsePositiveInt(string name, string value)
    {
        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new UserFacingException($"'{value}' is not a valid value for {name}. Use a positive integer number of seconds.");
    }
}

public sealed class ResolvedSchemaCacheSettings(bool enabled, string cacheDirectory, TimeSpan ttl)
{
    public bool Enabled { get; } = enabled;
    public string CacheDirectory { get; } = cacheDirectory;
    public TimeSpan Ttl { get; } = ttl;
}
