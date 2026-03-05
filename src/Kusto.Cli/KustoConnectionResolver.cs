namespace Kusto.Cli;

public sealed class KustoConnectionResolver : IKustoConnectionResolver
{
    public ResolvedCluster ResolveCluster(KustoConfig config, string? clusterReference)
    {
        if (string.IsNullOrWhiteSpace(clusterReference))
        {
            if (string.IsNullOrWhiteSpace(config.DefaultClusterUrl))
            {
                throw new UserFacingException("No default cluster is configured. Set one with 'kusto cluster set-default <name|url>'.");
            }

            var knownDefault = ClusterUtilities.FindKnownCluster(config, config.DefaultClusterUrl);
            return new ResolvedCluster(knownDefault?.Name, ClusterUtilities.NormalizeClusterUrl(config.DefaultClusterUrl));
        }

        var knownCluster = ClusterUtilities.FindKnownCluster(config, clusterReference);
        if (knownCluster is not null)
        {
            return new ResolvedCluster(knownCluster.Name, ClusterUtilities.NormalizeClusterUrl(knownCluster.Url));
        }

        if (Uri.TryCreate(clusterReference, UriKind.Absolute, out _))
        {
            return new ResolvedCluster(null, ClusterUtilities.NormalizeClusterUrl(clusterReference));
        }

        throw new UserFacingException($"Cluster '{clusterReference}' was not found.");
    }

    public string ResolveDatabase(KustoConfig config, string clusterUrl, string? databaseOverride)
    {
        if (!string.IsNullOrWhiteSpace(databaseOverride))
        {
            return databaseOverride;
        }

        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        if (config.DefaultDatabases.TryGetValue(normalizedClusterUrl, out var defaultDatabase) &&
            !string.IsNullOrWhiteSpace(defaultDatabase))
        {
            return defaultDatabase;
        }

        throw new UserFacingException("No database was specified and no default database is configured for the selected cluster.");
    }
}
