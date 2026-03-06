using Azure.Identity;

namespace Kusto.Cli.Tests;

public sealed class KustoCloudEnvironmentTests
{
    [Fact]
    public void TryResolve_PublicCluster_UsesPublicCloudSettings()
    {
        var cloud = KustoCloudEnvironmentResolver.TryResolve("https://help.kusto.windows.net");

        Assert.NotNull(cloud);
        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud.AbsoluteUri, cloud!.AuthorityHost.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.windows.net/.default", cloud.Scope);
        Assert.Equal("https://dataexplorer.azure.com", cloud.ExplorerBase);
    }

    [Fact]
    public void TryResolve_FabricCluster_UsesPublicCloudSettings()
    {
        var cloud = KustoCloudEnvironmentResolver.TryResolve("https://workspace.kusto.fabric.microsoft.com");

        Assert.NotNull(cloud);
        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud.AbsoluteUri, cloud!.AuthorityHost.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.windows.net/.default", cloud.Scope);
        Assert.Equal("https://dataexplorer.azure.com", cloud.ExplorerBase);
    }

    [Fact]
    public void TryResolve_UsGovernmentCluster_UsesGovernmentCloudSettings()
    {
        var cloud = KustoCloudEnvironmentResolver.TryResolve("https://mycluster.kusto.usgovcloudapi.net");

        Assert.NotNull(cloud);
        Assert.Equal(AzureAuthorityHosts.AzureGovernment.AbsoluteUri, cloud!.AuthorityHost.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.usgovcloudapi.net/.default", cloud.Scope);
        Assert.Equal("https://dataexplorer.azure.us", cloud.ExplorerBase);
    }

    [Fact]
    public void TryResolve_ChinaCluster_UsesChinaCloudSettings()
    {
        var cloud = KustoCloudEnvironmentResolver.TryResolve("https://mycluster.kusto.chinacloudapi.cn");

        Assert.NotNull(cloud);
        Assert.Equal(AzureAuthorityHosts.AzureChina.AbsoluteUri, cloud!.AuthorityHost.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.chinacloudapi.cn/.default", cloud.Scope);
        Assert.Equal("https://dataexplorer.azure.cn", cloud.ExplorerBase);
    }

    [Fact]
    public void ResolveForAuthentication_UnknownCluster_FallsBackToPublicCloud()
    {
        var cloud = KustoCloudEnvironmentResolver.ResolveForAuthentication("https://example.com");

        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud.AbsoluteUri, cloud.AuthorityHost.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.windows.net/.default", cloud.Scope);
        Assert.Equal("https://dataexplorer.azure.com", cloud.ExplorerBase);
    }
}
