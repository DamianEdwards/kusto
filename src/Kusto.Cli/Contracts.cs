using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public interface IConfigStore
{
    Task<KustoConfig> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(KustoConfig config, CancellationToken cancellationToken);
}

public interface IKustoConnectionResolver
{
    ResolvedCluster ResolveCluster(KustoConfig config, string? clusterReference);
    string ResolveDatabase(KustoConfig config, string clusterUrl, string? databaseOverride);
}

public interface ITokenProvider
{
    Task<string> GetTokenAsync(string clusterUrl, CancellationToken cancellationToken);
}

public interface IKustoService
{
    Task<TabularData> ExecuteManagementCommandAsync(
        string clusterUrl,
        string? database,
        string command,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken);
    Task<QueryExecutionResult> ExecuteQueryAsync(
        string clusterUrl,
        string database,
        string query,
        bool includeStatistics,
        CancellationToken cancellationToken);
}

public interface IOutputFormatter
{
    string Format(CliOutput output, OutputFormat format);
}

public interface ITableSchemaProvider
{
    Task<Dictionary<string, string?>> GetTablePropertiesAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        CancellationToken cancellationToken);
}

public enum OutputFormat
{
    Human,
    Json,
    Markdown
}

public sealed class CliRuntime(
    ILoggerFactory loggerFactory,
    ILogger logger,
    HttpClient httpClient,
    IConfigStore configStore,
    IKustoConnectionResolver connectionResolver,
    IKustoService kustoService,
    ITableSchemaProvider tableSchemaProvider,
    IOutputFormatter outputFormatter) : IDisposable
{
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ILogger Logger { get; } = logger;
    public HttpClient HttpClient { get; } = httpClient;
    public IConfigStore ConfigStore { get; } = configStore;
    public IKustoConnectionResolver ConnectionResolver { get; } = connectionResolver;
    public IKustoService KustoService { get; } = kustoService;
    public ITableSchemaProvider TableSchemaProvider { get; } = tableSchemaProvider;
    public IOutputFormatter OutputFormatter { get; } = outputFormatter;

    public void Dispose()
    {
        HttpClient.Dispose();
        LoggerFactory.Dispose();
    }
}
