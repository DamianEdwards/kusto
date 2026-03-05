namespace Kusto.Cli.Tests;

public sealed class ConnectionResolverTests
{
    private readonly KustoConnectionResolver _resolver = new();

    [Fact]
    public void ResolveCluster_UsesDefaultClusterWhenNoOverride()
    {
        var config = new KustoConfig
        {
            DefaultClusterUrl = "https://help.kusto.windows.net",
            Clusters =
            [
                new KnownCluster { Name = "help", Url = "https://help.kusto.windows.net" }
            ]
        };

        var resolved = _resolver.ResolveCluster(config, null);
        Assert.Equal("help", resolved.Name);
        Assert.Equal("https://help.kusto.windows.net", resolved.Url);
    }

    [Fact]
    public void ResolveDatabase_UsesClusterDefault()
    {
        var config = new KustoConfig
        {
            DefaultDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://help.kusto.windows.net"] = "Samples"
            }
        };

        var resolved = _resolver.ResolveDatabase(config, "https://help.kusto.windows.net", null);
        Assert.Equal("Samples", resolved);
    }

    [Fact]
    public void ResolveDatabase_UsesExplicitOverride()
    {
        var config = new KustoConfig();
        var resolved = _resolver.ResolveDatabase(config, "https://help.kusto.windows.net", "ExplicitDb");
        Assert.Equal("ExplicitDb", resolved);
    }
}
