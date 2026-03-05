using System.Text;
using System.Text.Json;

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
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            buffer.AppendLine(output.Message);
        }

        if (output.Properties is not null && output.Properties.Count > 0)
        {
            foreach (var pair in output.Properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                buffer.AppendLine($"{pair.Key}: {pair.Value}");
            }
        }

        if (output.Table is not null)
        {
            if (output.Table.Columns.Count > 0)
            {
                buffer.AppendLine(string.Join('\t', output.Table.Columns));
            }

            foreach (var row in output.Table.Rows)
            {
                buffer.AppendLine(string.Join('\t', row.Select(value => value ?? string.Empty)));
            }
        }

        return buffer.ToString().TrimEnd();
    }

    private static string FormatMarkdown(CliOutput output)
    {
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(output.Message))
        {
            buffer.AppendLine(output.Message);
            buffer.AppendLine();
        }

        if (output.Properties is not null && output.Properties.Count > 0)
        {
            buffer.AppendLine("| Property | Value |");
            buffer.AppendLine("|---|---|");
            foreach (var pair in output.Properties.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                buffer.AppendLine($"| {EscapeMarkdown(pair.Key)} | {EscapeMarkdown(pair.Value ?? string.Empty)} |");
            }
            buffer.AppendLine();
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

        return buffer.ToString().TrimEnd();
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
