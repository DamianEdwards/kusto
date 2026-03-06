using System.IO.Compression;
using System.Text;

namespace Kusto.Cli.Tests;

public sealed class KustoWebExplorerUrlBuilderTests
{
    [Fact]
    public void Build_PublicCluster_RoundTripsQuery()
    {
        var url = KustoWebExplorerUrlBuilder.Build(
            "https://help.kusto.windows.net/",
            "Samples",
            "StormEvents | take 10");

        Assert.NotNull(url);
        Assert.StartsWith(
            "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=",
            url);
        Assert.Equal("StormEvents | take 10", DecodeQueryFromUrl(url!));
    }

    [Fact]
    public void Build_FabricCluster_UsesPublicExplorer()
    {
        var url = KustoWebExplorerUrlBuilder.Build(
            "https://workspace.kusto.fabric.microsoft.com",
            "db1",
            "T | take 1");

        Assert.NotNull(url);
        Assert.StartsWith(
            "https://dataexplorer.azure.com/clusters/workspace.kusto.fabric.microsoft.com/databases/db1?query=",
            url);
    }

    [Fact]
    public void Build_UsGovCluster_UsesGovExplorer()
    {
        var url = KustoWebExplorerUrlBuilder.Build(
            "https://mycluster.kusto.usgovcloudapi.net",
            "db1",
            "T | take 1");

        Assert.NotNull(url);
        Assert.StartsWith(
            "https://dataexplorer.azure.us/clusters/mycluster.kusto.usgovcloudapi.net/databases/db1?query=",
            url);
    }

    [Fact]
    public void Build_ChinaCluster_UsesChinaExplorer()
    {
        var url = KustoWebExplorerUrlBuilder.Build(
            "https://mycluster.kusto.chinacloudapi.cn",
            "db1",
            "T | take 1");

        Assert.NotNull(url);
        Assert.StartsWith(
            "https://dataexplorer.azure.cn/clusters/mycluster.kusto.chinacloudapi.cn/databases/db1?query=",
            url);
    }

    [Fact]
    public void Build_UnsupportedDomain_ReturnsNull()
    {
        var url = KustoWebExplorerUrlBuilder.Build("https://example.com", "db", "query");

        Assert.Null(url);
    }

    [Fact]
    public void Build_QueryExceedingMaxLength_ReturnsNull()
    {
        var longQuery = string.Concat(Enumerable.Range(0, 10000).Select(i => i.ToString("X4")));

        var url = KustoWebExplorerUrlBuilder.Build("https://help.kusto.windows.net", "Samples", longQuery);

        Assert.Null(url);
    }

    private static string DecodeQueryFromUrl(string url)
    {
        var uri = new Uri(url);
        var encodedQuery = GetRequiredQueryParameter(uri, "query");
        var compressed = Convert.FromBase64String(Uri.UnescapeDataString(encodedQuery));

        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string GetRequiredQueryParameter(Uri uri, string parameterName)
    {
        var prefix = $"{parameterName}=";
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith(prefix, StringComparison.Ordinal))
            {
                return segment[prefix.Length..];
            }
        }

        throw new InvalidOperationException($"Query parameter '{parameterName}' was not found.");
    }
}
