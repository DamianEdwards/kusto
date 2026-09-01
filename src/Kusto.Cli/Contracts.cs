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
    Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit interactive/broker authentication operations for WAM-configured clusters.
/// Query-time token acquisition never goes through this seam; it stays silent via
/// <see cref="ITokenProvider"/>.
/// </summary>
public interface IClusterAuthenticationService
{
    Task LoginAsync(ResolvedCluster cluster, CancellationToken cancellationToken);
    Task<bool> LogoutAsync(ResolvedCluster cluster, CancellationToken cancellationToken);
}

public interface IKustoService
{
    Task<TabularData> ExecuteManagementCommandAsync(
        ResolvedCluster cluster,
        string? database,
        string command,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken);
    Task<QueryExecutionResult> ExecuteQueryAsync(
        ResolvedCluster cluster,
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
    Task<TableSchemaDetails> GetTableSchemaDetailsAsync(
        KustoConfig config,
        ResolvedCluster cluster,
        string database,
        string tableName,
        bool refreshOfflineData,
        CancellationToken cancellationToken);
}

public interface ITableOfflineDataManager
{
    Task<CliOutput> ShowTableNotesAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        int? noteId,
        CancellationToken cancellationToken);
    Task<CliOutput> AddTableNoteAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        string note,
        CancellationToken cancellationToken);
    Task<CliOutput> DeleteTableNoteAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        int noteId,
        CancellationToken cancellationToken);
    Task<CliOutput> ClearTableNotesAsync(
        KustoConfig config,
        string? clusterUrl,
        string? database,
        string? tableName,
        CancellationToken cancellationToken);
    Task<CliOutput> ExportOfflineDataAsync(
        KustoConfig config,
        string filePath,
        CancellationToken cancellationToken);
    Task<CliOutput> ImportOfflineDataAsync(
        KustoConfig config,
        string filePath,
        CancellationToken cancellationToken);
    Task<CliOutput> PurgeOfflineDataAsync(
        KustoConfig config,
        CancellationToken cancellationToken);
    Task<CliOutput> ClearOfflineDataAsync(
        KustoConfig config,
        string? clusterUrl,
        string? database,
        string? tableName,
        CancellationToken cancellationToken);
}

public interface IConfirmationPrompt
{
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class CliRuntime(
    ILoggerFactory loggerFactory,
    ILogger logger,
    HttpClient httpClient,
    IConfigStore configStore,
    IKustoConnectionResolver connectionResolver,
    IKustoService kustoService,
    IClusterAuthenticationService clusterAuthenticationService,
    ITableSchemaProvider tableSchemaProvider,
    ITableOfflineDataManager tableOfflineDataManager,
    IConfirmationPrompt confirmationPrompt,
    IOutputFormatter outputFormatter,
    params IDisposable[] additionalDisposables) : IDisposable
{
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public ILogger Logger { get; } = logger;
    public HttpClient HttpClient { get; } = httpClient;
    public IConfigStore ConfigStore { get; } = configStore;
    public IKustoConnectionResolver ConnectionResolver { get; } = connectionResolver;
    public IKustoService KustoService { get; } = kustoService;
    public IClusterAuthenticationService ClusterAuthenticationService { get; } = clusterAuthenticationService;
    public ITableSchemaProvider TableSchemaProvider { get; } = tableSchemaProvider;
    public ITableOfflineDataManager TableOfflineDataManager { get; } = tableOfflineDataManager;
    public IConfirmationPrompt ConfirmationPrompt { get; } = confirmationPrompt;
    public IOutputFormatter OutputFormatter { get; } = outputFormatter;

    private readonly IDisposable[] _additionalDisposables = additionalDisposables ?? [];

    public void Dispose()
    {
        foreach (var disposable in _additionalDisposables)
        {
            disposable.Dispose();
        }

        HttpClient.Dispose();
        LoggerFactory.Dispose();
    }
}

public enum OutputFormat
{
    Human,
    Json,
    Markdown,
    Csv
}
