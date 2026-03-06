using Azure.Identity;

namespace Kusto.Cli;

internal sealed record KustoCloudEnvironment(
    Uri AuthorityHost,
    string Scope,
    string? ExplorerBase);

internal static class KustoCloudEnvironmentResolver
{
    private static readonly KustoCloudEnvironment PublicCloud = new(
        AzureAuthorityHosts.AzurePublicCloud,
        "https://kusto.kusto.windows.net/.default",
        "https://dataexplorer.azure.com");

    private static readonly KustoCloudEnvironment UsGovernmentCloud = new(
        AzureAuthorityHosts.AzureGovernment,
        "https://kusto.kusto.usgovcloudapi.net/.default",
        "https://dataexplorer.azure.us");

    private static readonly KustoCloudEnvironment ChinaCloud = new(
        AzureAuthorityHosts.AzureChina,
        "https://kusto.kusto.chinacloudapi.cn/.default",
        "https://dataexplorer.azure.cn");

    private static readonly (string Suffix, KustoCloudEnvironment Cloud)[] CloudMappings =
    [
        (".kusto.windows.net", PublicCloud),
        (".kustodev.windows.net", PublicCloud),
        (".kustomfa.windows.net", PublicCloud),
        (".kusto.data.microsoft.com", PublicCloud),
        (".kusto.fabric.microsoft.com", PublicCloud),
        (".kusto.azuresynapse.net", PublicCloud),
        (".kusto.usgovcloudapi.net", UsGovernmentCloud),
        (".kustomfa.usgovcloudapi.net", UsGovernmentCloud),
        (".kusto.chinacloudapi.cn", ChinaCloud),
        (".kustomfa.chinacloudapi.cn", ChinaCloud),
        (".kusto.azuresynapse.azure.cn", ChinaCloud)
    ];

    public static KustoCloudEnvironment ResolveForAuthentication(string clusterUrl)
    {
        return TryResolve(clusterUrl) ?? PublicCloud;
    }

    public static KustoCloudEnvironment? TryResolve(string clusterUrl)
    {
        if (!Uri.TryCreate(clusterUrl, UriKind.Absolute, out var clusterUri) ||
            string.IsNullOrWhiteSpace(clusterUri.Host))
        {
            return null;
        }

        foreach (var mapping in CloudMappings)
        {
            if (clusterUri.Host.EndsWith(mapping.Suffix, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Cloud;
            }
        }

        return null;
    }
}
