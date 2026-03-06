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
    [JsonIgnore]
    public bool IsQueryResultTable { get; init; }
}

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

public sealed class QueryExecutionResult(TabularData table, string? webExplorerUrl, QueryStatistics? statistics)
{
    public TabularData Table { get; } = table;
    public string? WebExplorerUrl { get; } = webExplorerUrl;
    public QueryStatistics? Statistics { get; } = statistics;
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
}

public sealed class KnownCluster
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class ResolvedCluster(string? name, string url)
{
    public string? Name { get; } = name;
    public string Url { get; } = url;
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
