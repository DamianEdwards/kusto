using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kusto.Cli;

public sealed class OutputFormatter : IOutputFormatter
{
    public string Format(CliOutput output, OutputFormat format)
    {
        return format switch
        {
            OutputFormat.Json => JsonSerializer.Serialize(output, KustoJsonSerializerContext.Default.CliOutput),
            OutputFormat.Markdown => FormatMarkdown(output),
            _ => FormatHuman(output)
        };
    }

    private static string FormatHuman(CliOutput output)
    {
        using var writer = new StringWriter();
        var useAnsi = ConsoleRendering.ShouldUseAnsiForStandardOutput();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = useAnsi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = useAnsi ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = useAnsi ? InteractionSupport.Yes : InteractionSupport.No
        });

        var wroteSection = false;
        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            console.MarkupLine(Markup.Escape(output.Message));
            wroteSection = true;
        }

        var displayProperties = BuildDisplayProperties(output);
        if (displayProperties.Count > 0)
        {
            if (wroteSection)
            {
                console.WriteLine();
            }

            console.Write(CreatePropertiesTable(displayProperties));
            wroteSection = true;
        }

        if (output.Statistics is not null)
        {
            var statisticsProperties = FlattenStatistics(output.Statistics);
            if (statisticsProperties.Count > 0)
            {
                if (wroteSection)
                {
                    console.WriteLine();
                }

                console.MarkupLine("Statistics");
                console.Write(CreatePropertiesTable(statisticsProperties));
                wroteSection = true;
            }
        }

        if (output.Table is not null)
        {
            if (wroteSection)
            {
                console.WriteLine();
            }

            var table = CreateDataTable(output.Table, output.IsQueryResultTable, useAnsi);

            console.Write(table);
            wroteSection = true;
        }

        if (!string.IsNullOrWhiteSpace(output.WebExplorerUrl))
        {
            if (wroteSection)
            {
                console.WriteLine();
            }

            WriteWebExplorerLink(console, output.WebExplorerUrl, useAnsi);
        }

        return writer.ToString().TrimEnd();
    }

    private static string FormatMarkdown(CliOutput output)
    {
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            buffer.AppendLine(output.Message);
            buffer.AppendLine();
        }

        var displayProperties = BuildDisplayProperties(output);
        if (displayProperties.Count > 0)
        {
            AppendPropertiesTable(buffer, displayProperties);
            buffer.AppendLine();
        }

        if (output.Statistics is not null)
        {
            var statisticsProperties = FlattenStatistics(output.Statistics);
            if (statisticsProperties.Count > 0)
            {
                buffer.AppendLine("### Statistics");
                buffer.AppendLine();
                AppendPropertiesTable(buffer, statisticsProperties);
                buffer.AppendLine();
            }
        }

        if (output.Table is not null)
        {
            if (output.Table.Columns.Count > 0)
            {
                buffer.AppendLine($"| {string.Join(" | ", output.Table.Columns.Select(EscapeMarkdown))} |");
                buffer.AppendLine($"| {string.Join(" | ", output.Table.Columns.Select(_ => "---"))} |");
            }

            foreach (var row in output.Table.Rows)
            {
                buffer.AppendLine($"| {string.Join(" | ", row.Select(value => EscapeMarkdown(value ?? string.Empty)))} |");
            }
        }

        if (!string.IsNullOrWhiteSpace(output.WebExplorerUrl))
        {
            if (buffer.Length > 0)
            {
                buffer.AppendLine();
            }

            buffer.AppendLine($"[Open in Web Explorer]({output.WebExplorerUrl})");
        }

        return buffer.ToString().TrimEnd();
    }

    private static Table CreatePropertiesTable(IReadOnlyDictionary<string, string?> properties)
    {
        var propertiesTable = new Table().Border(TableBorder.Rounded).Expand();
        propertiesTable.AddColumn(new TableColumn("Property"));
        propertiesTable.AddColumn(new TableColumn("Value"));

        foreach (var pair in properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            propertiesTable.AddRow(
                Markup.Escape(pair.Key),
                Markup.Escape(pair.Value ?? string.Empty));
        }

        return propertiesTable;
    }

    private static Dictionary<string, string?> BuildDisplayProperties(CliOutput output)
    {
        return output.Properties is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(output.Properties, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> FlattenStatistics(QueryStatistics statistics)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        AddStatistic(properties, "ExecutionTimeSec", FormatNumber(statistics.ExecutionTimeSec));

        if (statistics.Cpu is not null)
        {
            AddStatistic(properties, "Cpu.Total", statistics.Cpu.Total);
            AddStatistic(properties, "Cpu.QueryExecution", statistics.Cpu.QueryExecution);
            AddStatistic(properties, "Cpu.QueryPlanning", statistics.Cpu.QueryPlanning);
        }

        AddStatistic(properties, "MemoryPeakPerNodeMb", FormatNumber(statistics.MemoryPeakPerNodeMb));

        if (statistics.Cache is not null)
        {
            AddStatistic(properties, "Cache.HotHitMb", FormatNumber(statistics.Cache.HotHitMb));
            AddStatistic(properties, "Cache.HotMissMb", FormatNumber(statistics.Cache.HotMissMb));
        }

        if (statistics.Network is not null)
        {
            AddStatistic(properties, "Network.CrossClusterMb", FormatNumber(statistics.Network.CrossClusterMb));
            AddStatistic(properties, "Network.InterClusterMb", FormatNumber(statistics.Network.InterClusterMb));
        }

        if (statistics.Extents is not null)
        {
            AddStatistic(properties, "Extents.Scanned", FormatNumber(statistics.Extents.Scanned));
            AddStatistic(properties, "Extents.Total", FormatNumber(statistics.Extents.Total));
        }

        if (statistics.Rows is not null)
        {
            AddStatistic(properties, "Rows.Scanned", FormatNumber(statistics.Rows.Scanned));
            AddStatistic(properties, "Rows.Total", FormatNumber(statistics.Rows.Total));
        }

        if (statistics.Result is not null)
        {
            AddStatistic(properties, "Result.RowCount", FormatNumber(statistics.Result.RowCount));
            AddStatistic(properties, "Result.SizeKb", FormatNumber(statistics.Result.SizeKb));
        }

        if (statistics.CrossClusterBreakdown is not null)
        {
            foreach (var cluster in statistics.CrossClusterBreakdown.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CpuTotal", cluster.Value.CpuTotal);
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.MemoryPeakMb", FormatNumber(cluster.Value.MemoryPeakMb));
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CacheHitMb", FormatNumber(cluster.Value.CacheHitMb));
                AddStatistic(properties, $"CrossClusterBreakdown.{cluster.Key}.CacheMissMb", FormatNumber(cluster.Value.CacheMissMb));
            }
        }

        return properties;
    }

    private static void AppendPropertiesTable(StringBuilder buffer, IReadOnlyDictionary<string, string?> properties)
    {
        buffer.AppendLine("| Property | Value |");
        buffer.AppendLine("|---|---|");
        foreach (var pair in properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            buffer.AppendLine($"| {EscapeMarkdown(pair.Key)} | {EscapeMarkdown(pair.Value ?? string.Empty)} |");
        }
    }

    private static void AddStatistic(IDictionary<string, string?> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value;
        }
    }

    private static string? FormatNumber(double? value)
    {
        return value?.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string? FormatNumber(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static void WriteWebExplorerLink(IAnsiConsole console, string url, bool useAnsi)
    {
        if (useAnsi)
        {
            console.MarkupLine($"[link={Markup.Escape(url)}]Open in Web Explorer[/]");
            return;
        }

        console.MarkupLine($"Open in Web Explorer: {Markup.Escape(url)}");
    }

    private static Table CreateDataTable(TabularData data, bool isQueryResultTable, bool useAnsi)
    {
        var table = new Table
        {
            Border = isQueryResultTable ? TableBorder.MinimalHeavyHead : TableBorder.Rounded,
            Expand = !isQueryResultTable,
            ShowRowSeparators = isQueryResultTable,
            UseSafeBorder = !useAnsi
        };

        if (data.Columns.Count == 0)
        {
            table.AddColumn(new TableColumn("Value"));
            foreach (var row in data.Rows)
            {
                table.AddRow(Markup.Escape(string.Join(", ", row.Select(value => value ?? string.Empty))));
            }

            return table;
        }

        var rightAlignedColumns = new bool[data.Columns.Count];
        for (var i = 0; i < data.Columns.Count; i++)
        {
            var column = new TableColumn(Markup.Escape(data.Columns[i]));
            if (isQueryResultTable && ShouldRightAlignColumn(data, i))
            {
                rightAlignedColumns[i] = true;
            }

            table.AddColumn(column);
        }

        foreach (var row in data.Rows)
        {
            var rowCells = new IRenderable[data.Columns.Count];
            for (var i = 0; i < data.Columns.Count; i++)
            {
                var value = i < row.Count ? row[i] : string.Empty;
                IRenderable renderedCell = new Markup(Markup.Escape(value ?? string.Empty));
                if (isQueryResultTable && rightAlignedColumns[i])
                {
                    renderedCell = Align.Right(renderedCell);
                }

                rowCells[i] = renderedCell;
            }

            table.AddRow(rowCells);
        }

        return table;
    }

    private static bool ShouldRightAlignColumn(TabularData data, int columnIndex)
    {
        var hasValue = false;
        foreach (var row in data.Rows)
        {
            if (columnIndex >= row.Count)
            {
                continue;
            }

            var value = row[columnIndex];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            hasValue = true;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return hasValue;
    }
}
