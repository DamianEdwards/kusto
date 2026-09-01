using System.Text.Json;

namespace Kusto.Cli;

public sealed class FileConfigStore(string? configPath = null) : IConfigStore
{
    private readonly string _configPath = string.IsNullOrWhiteSpace(configPath)
            ? ResolveConfigPath()
            : configPath;

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

    internal static string ResolveConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("KUSTO_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppPaths.GetAppHomeDirectory(), "config.json");
    }
}
