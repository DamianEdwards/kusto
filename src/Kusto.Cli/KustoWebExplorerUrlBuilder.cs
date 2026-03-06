using System.IO.Compression;
using System.Text;

namespace Kusto.Cli;

internal static class KustoWebExplorerUrlBuilder
{
    private const int MaxUrlLength = 8000;

    public static string? Build(string clusterUrl, string database, string query)
    {
        if (!Uri.TryCreate(clusterUrl, UriKind.Absolute, out var clusterUri) ||
            string.IsNullOrWhiteSpace(clusterUri.Host))
        {
            return null;
        }

        var explorerBase = KustoCloudEnvironmentResolver.TryResolve(clusterUrl)?.ExplorerBase;
        if (explorerBase is null)
        {
            return null;
        }

        var encodedQuery = Uri.EscapeDataString(Convert.ToBase64String(CompressQuery(query.Trim())));
        var url = $"{explorerBase}/clusters/{clusterUri.Host}/databases/{Uri.EscapeDataString(database)}?query={encodedQuery}";

        return url.Length <= MaxUrlLength ? url : null;
    }
    private static byte[] CompressQuery(string query)
    {
        var queryBytes = Encoding.UTF8.GetBytes(query);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(queryBytes, 0, queryBytes.Length);
        }

        return output.ToArray();
    }
}
