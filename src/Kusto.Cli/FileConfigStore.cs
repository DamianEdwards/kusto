using System.Text.Json;

namespace Kusto.Cli;

public sealed class FileConfigStore(string? configPath = null) : IConfigStore
{
    private readonly string _configPath = string.IsNullOrWhiteSpace(configPath)
            ? ResolveDefaultConfigPath()
            : configPath;

    public string ConfigPath => _configPath;

    public async Task<KustoConfig> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            return new KustoConfig();
        }

        try
        {
            await using var stream = File.OpenRead(_configPath);
            var config = await JsonSerializer.DeserializeAsync(
                stream,
                KustoJsonSerializerContext.Default.KustoConfig,
                cancellationToken);

            // Keep load recoverable when a newer CLI wrote an auth mode this version
            // does not understand. Write paths still validate authentication strictly.
            return ClusterUtilities.NormalizeConfig(config, validateAuthentication: false);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"The config file at '{_configPath}' is malformed JSON.", ex);
        }
    }

    public async Task SaveAsync(KustoConfig config, CancellationToken cancellationToken)
    {
        // Authentication is validated where it is created or changed. Preserve auth
        // modes from newer CLI versions when an unrelated setting is saved.
        var normalized = ClusterUtilities.NormalizeConfig(
            config,
            validateAuthentication: false);
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_configPath);
        await JsonSerializer.SerializeAsync(
            stream,
            normalized,
            KustoJsonSerializerContext.Default.KustoConfig,
            cancellationToken);
    }

    private static string ResolveDefaultConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("KUSTO_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfilePath, ".kusto", "config.json");
    }

    /// <summary>
    /// Resolves the effective configuration directory for the given config path (or the
    /// default path when none is supplied). Used to anchor the authentication record store
    /// and the deterministic protected token cache name.
    /// </summary>
    public static string ResolveConfigDirectory(string? configPath)
    {
        var effectivePath = string.IsNullOrWhiteSpace(configPath)
            ? ResolveDefaultConfigPath()
            : configPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(effectivePath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directory = Path.Combine(userProfilePath, ".kusto");
        }

        return directory;
    }
}
