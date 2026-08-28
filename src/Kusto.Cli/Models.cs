using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kusto.Cli;

public sealed class CliOutput
{
    public string? Message { get; init; }
    public TabularData? Table { get; init; }
    public Dictionary<string, string?>? Properties { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebExplorerUrl { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryStatistics? Statistics { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryVisualization? Visualization { get; init; }
    [JsonIgnore]
    public string? ChartHint { get; init; }
    [JsonIgnore]
    public string? ChartMessage { get; init; }
    [JsonIgnore]
    public string? HumanChart { get; init; }
    [JsonIgnore]
    public string? HumanChartAnsi { get; init; }
    [JsonIgnore]
    public string? MarkdownChart { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChartOutputPath { get; init; }
    [JsonIgnore]
    public bool IsQueryResultTable { get; init; }
}

internal sealed record HumanChartRenderResult(string PlainText, string? AnsiText);

public sealed class TabularData(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
{
    public static TabularData Empty { get; } = new([], []);

    public IReadOnlyList<string> Columns { get; } = columns;
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; } = rows;

    public bool TryGetColumnIndex(string columnName, out int columnIndex)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], columnName, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                return true;
            }
        }

        columnIndex = -1;
        return false;
    }
}

public sealed class TableSchemaDetails
{
    public Dictionary<string, string?> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public TabularData Columns { get; init; } = TabularData.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotesMessage { get; init; }
}

public sealed class QueryExecutionResult(
    TabularData table,
    string? webExplorerUrl,
    QueryStatistics? statistics,
    QueryVisualization? visualization)
{
    public TabularData Table { get; } = table;
    public string? WebExplorerUrl { get; } = webExplorerUrl;
    public QueryStatistics? Statistics { get; } = statistics;
    public QueryVisualization? Visualization { get; } = visualization;
}

public sealed class QueryVisualization
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Visualization { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? XTitle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YTitle { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? XColumn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? YColumns { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Series { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Legend { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? YMin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? YMax { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? AdditionalProperties { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Raw { get; init; }
}

public sealed class QueryStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExecutionTimeSec { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryCpuStatistics? Cpu { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MemoryPeakPerNodeMb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryCacheStatistics? Cache { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryNetworkStatistics? Network { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryCountStatistics? Extents { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryCountStatistics? Rows { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueryResultStatistics? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, QueryCrossClusterStatistics>? CrossClusterBreakdown { get; init; }
}

public sealed class QueryCpuStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Total { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryExecution { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryPlanning { get; init; }
}

public sealed class QueryCacheStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HotHitMb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HotMissMb { get; init; }
}

public sealed class QueryNetworkStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CrossClusterMb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InterClusterMb { get; init; }
}

public sealed class QueryCountStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Scanned { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }
}

public sealed class QueryResultStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RowCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SizeKb { get; init; }
}

public sealed class QueryCrossClusterStatistics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CpuTotal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MemoryPeakMb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheHitMb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheMissMb { get; init; }
}

public sealed class KustoConfig
{
    public List<KnownCluster> Clusters { get; set; } = [];
    public string? DefaultClusterUrl { get; set; }
    public Dictionary<string, string> DefaultDatabases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SchemaCacheConfig SchemaCache { get; set; } = new();
}

public sealed class KnownCluster
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClusterAuthentication? Authentication { get; set; }
}

/// <summary>
/// Optional per-cluster authentication descriptor. When absent (null) the cluster
/// uses the default <c>DefaultAzureCredential</c> flow. When present with
/// <see cref="Mode"/> == <c>wam</c> the cluster is bound to a specific Windows work
/// account through the native broker. Never contains tokens or other secrets.
/// </summary>
public sealed class ClusterAuthentication
{
    public string Mode { get; set; } = ClusterAuthenticationModes.Default;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class ClusterAuthenticationModes
{
    public const string Default = "default";
    public const string Wam = "wam";

    public static bool IsDefault(ClusterAuthentication? authentication) =>
        authentication is null ||
        string.Equals(authentication.Mode, Default, StringComparison.OrdinalIgnoreCase);

    public static bool IsWam(ClusterAuthentication? authentication) =>
        authentication is not null && string.Equals(authentication.Mode, Wam, StringComparison.OrdinalIgnoreCase);
}

public sealed class SchemaCacheConfig
{
    public bool Enabled { get; set; } = true;
    public string? Path { get; set; }
    public int TtlSeconds { get; set; } = SchemaCacheSettingsResolver.DefaultTtlSeconds;
    public List<SchemaCacheOverride> Overrides { get; set; } = [];
}

public sealed class SchemaCacheOverride
{
    public string ClusterUrl { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = SchemaCacheSettingsResolver.DefaultTtlSeconds;
}

public sealed class OfflineTableDataExport
{
    public int FormatVersion { get; set; } = 1;
    public List<DatabaseSchemaCacheEntry> Entries { get; set; } = [];
}

public sealed class ResolvedCluster(string? name, string url, ClusterAuthentication? authentication = null)
{
    public string? Name { get; } = name;
    public string Url { get; } = url;

    /// <summary>
    /// The authentication descriptor resolved for this cluster. Never null: clusters
    /// without an explicit descriptor resolve to the default (<c>DefaultAzureCredential</c>)
    /// mode so token acquisition can route explicitly per request.
    /// </summary>
    public ClusterAuthentication Authentication { get; } = authentication ?? new ClusterAuthentication();
}

/// <summary>
/// Validated, non-secret Kusto authentication metadata read from a cluster's
/// unauthenticated <c>/v1/rest/auth/metadata</c> endpoint.
/// </summary>
internal sealed record KustoAuthMetadata(string ClientId, Uri AuthorityHost, string Resource, string LoginEndpoint);

internal sealed class KustoAuthMetadataEnvelope
{
    [JsonPropertyName("AzureAD")]
    public KustoAzureAdMetadata? AzureAd { get; set; }
}

internal sealed class KustoAzureAdMetadata
{
    [JsonPropertyName("LoginEndpoint")]
    public string? LoginEndpoint { get; set; }
    [JsonPropertyName("KustoClientAppId")]
    public string? KustoClientAppId { get; set; }
    [JsonPropertyName("KustoServiceResourceId")]
    public string? KustoServiceResourceId { get; set; }
}

internal sealed class KustoRequestPayload
{
    public string Db { get; set; } = string.Empty;
    public string Csl { get; set; } = string.Empty;
    public KustoRequestProperties? Properties { get; set; }
}

internal sealed class KustoRequestProperties
{
    [JsonPropertyName("Parameters")]
    public Dictionary<string, string>? Parameters { get; set; }
}

internal sealed class KustoResponsePayload
{
    public List<KustoResponseTable>? Tables { get; set; }
}

internal sealed class KustoResponseTable
{
    public string? TableName { get; set; }
    public string? TableKind { get; set; }
    public List<KustoResponseColumn>? Columns { get; set; }
    public List<List<JsonElement>>? Rows { get; set; }
}

internal sealed class KustoResponseColumn
{
    public string? ColumnName { get; set; }
    public string? DataType { get; set; }
}

internal sealed class ParsedKustoTable(string? tableName, string? tableKind, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
{
    public string? TableName { get; } = tableName;
    public string? TableKind { get; } = tableKind;
    public IReadOnlyList<string> Columns { get; } = columns;
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; } = rows;
}

public sealed class DatabaseSchemaCacheEntry
{
    public int CacheFormatVersion { get; set; } = 1;
    public string ClusterUrl { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public DateTimeOffset CachedAtUtc { get; set; }
    public string? SchemaVersion { get; set; }
    public string SchemaJson { get; set; } = string.Empty;
    public Dictionary<string, List<string>> TableNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal enum QueryChartKind
{
    Column,
    Bar,
    Line,
    Pie
}

internal enum QueryChartLayout
{
    Simple,
    Grouped,
    Stacked,
    Stacked100
}

internal sealed class QueryChartSeries(string name, IReadOnlyList<double> values)
{
    public string Name { get; } = name;
    public IReadOnlyList<double> Values { get; } = values;
}

internal sealed class QueryChartDefinition
{
    public QueryChartKind Kind { get; init; }
    public QueryChartLayout Layout { get; init; } = QueryChartLayout.Simple;
    public bool Horizontal { get; init; }
    public string? Title { get; init; }
    public string? XTitle { get; init; }
    public string? YTitle { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<QueryChartSeries> Series { get; init; } = [];

    /// <summary>
    /// When the X axis values are datetimes, this carries their parsed values so renderers
    /// can use a proper DateTime axis instead of formatted strings.
    /// </summary>
    public DateTime[]? DateTimeCategories { get; init; }
}

internal sealed class QueryChartCompatibility
{
    public QueryChartDefinition? HumanChart { get; init; }
    public string? HumanReason { get; init; }
    public QueryChartDefinition? MarkdownChart { get; init; }
    public string? MarkdownReason { get; init; }
}
