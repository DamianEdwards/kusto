using System.Text.Json;

namespace Kusto.Cli;

public sealed class CliOutput
{
    public string? Message { get; init; }
    public TabularData? Table { get; init; }
    public Dictionary<string, string?>? Properties { get; init; }
}

public sealed class TabularData
{
    public static TabularData Empty { get; } = new([], []);

    public TabularData(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; }

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
