using System.Text.Json;

namespace Kusto.Cli;

public sealed class FileConfigStore : IConfigStore
{
    private readonly string _configPath;

    public FileConfigStore(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath)
            ? ResolveDefaultConfigPath()
            : configPath;
    }

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

            return ClusterUtilities.NormalizeConfig(config);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"The config file at '{_configPath}' is malformed JSON.", ex);
        }
    }

    public async Task SaveAsync(KustoConfig config, CancellationToken cancellationToken)
    {
        var normalized = ClusterUtilities.NormalizeConfig(config);
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
}
