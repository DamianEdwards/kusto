using System.Text.Json;

namespace Kusto.Cli.Tests;

public sealed class OutputFormatterTests
{
    [Fact]
    public void FormatHuman_TableOutput_IncludesHeadersAndRows()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(
                ["TableName", "RowCount"],
                [
                    ["DotnetEvents", "42"],
                    ["DotnetMetrics", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("TableName", rendered, StringComparison.Ordinal);
        Assert.Contains("RowCount", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetEvents", rendered, StringComparison.Ordinal);
        Assert.Contains("DotnetMetrics", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_QueryTableOutput_UsesReadableTabularLayout()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            IsQueryResultTable = true,
            Table = new TabularData(
                ["Name", "Count"],
                [
                    ["alpha", "42"],
                    ["beta", "7"]
                ])
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Count", rendered, StringComparison.Ordinal);
        Assert.Contains("alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("beta", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHuman_PropertiesOutput_RendersPropertyTable()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Properties = new Dictionary<string, string?>
            {
                ["Name"] = "Samples",
                ["Default"] = "true"
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Property", rendered, StringComparison.Ordinal);
        Assert.Contains("Value", rendered, StringComparison.Ordinal);
        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Samples", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatJson_QueryOutput_IncludesWebExplorerUrlAndStatistics()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23,
                Network = new QueryNetworkStatistics
                {
                    CrossClusterMb = 5.2
                }
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Json);

        using var document = JsonDocument.Parse(rendered);
        Assert.Equal(
            "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            document.RootElement.GetProperty("webExplorerUrl").GetString());
        Assert.Equal(1.23, document.RootElement.GetProperty("statistics").GetProperty("executionTimeSec").GetDouble());
        Assert.Equal(5.2, document.RootElement.GetProperty("statistics").GetProperty("network").GetProperty("crossClusterMb").GetDouble());
        Assert.False(document.RootElement.GetProperty("statistics").TryGetProperty("rows", out _));
    }

    [Fact]
    public void FormatHuman_QueryOutput_HidesWebExplorerUrlByDefault()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23,
                Result = new QueryResultStatistics
                {
                    RowCount = 1
                }
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Human);

        Assert.Contains("Open in Web Explorer", rendered, StringComparison.Ordinal);
        Assert.Contains("Statistics", rendered, StringComparison.Ordinal);
        Assert.Contains("ExecutionTimeSec", rendered, StringComparison.Ordinal);
        Assert.Contains("Result.RowCount", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("WebExplorerUrl", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMarkdown_QueryOutput_HidesWebExplorerUrlByDefault()
    {
        var formatter = new OutputFormatter();
        var output = new CliOutput
        {
            Table = new TabularData(["Name"], [["alpha"]]),
            WebExplorerUrl = "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=abc",
            Statistics = new QueryStatistics
            {
                ExecutionTimeSec = 1.23
            }
        };

        var rendered = formatter.Format(output, OutputFormat.Markdown);

        Assert.Contains("### Statistics", rendered, StringComparison.Ordinal);
        Assert.Contains("ExecutionTimeSec", rendered, StringComparison.Ordinal);
        Assert.Contains("[Open in Web Explorer](", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("WebExplorerUrl", rendered, StringComparison.Ordinal);
    }
}
