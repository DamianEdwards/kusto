namespace Kusto.Cli;

public static class ClusterUtilities
{
    public static string NormalizeClusterUrl(string clusterUrl)
    {
        if (!Uri.TryCreate(clusterUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UserFacingException($"'{clusterUrl}' is not a valid cluster URL.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static KnownCluster? FindKnownCluster(KustoConfig config, string clusterReference)
    {
        foreach (var cluster in config.Clusters)
        {
            if (string.Equals(cluster.Name, clusterReference, StringComparison.OrdinalIgnoreCase))
            {
                return cluster;
            }
        }

        if (Uri.TryCreate(clusterReference, UriKind.Absolute, out _))
        {
            var normalizedReference = NormalizeClusterUrl(clusterReference);
            foreach (var cluster in config.Clusters)
            {
                if (string.Equals(NormalizeClusterUrl(cluster.Url), normalizedReference, StringComparison.OrdinalIgnoreCase))
                {
                    return cluster;
                }
            }
        }

        return null;
    }

    public static KustoConfig NormalizeConfig(KustoConfig? config)
    {
        if (config is null)
        {
            return new KustoConfig();
        }

        config.Clusters ??= [];
        config.SchemaCache ??= new SchemaCacheConfig();
        config.SchemaCache.Overrides ??= [];
        config.SchemaCache.Path = string.IsNullOrWhiteSpace(config.SchemaCache.Path)
            ? null
            : config.SchemaCache.Path.Trim();

        var normalizedDatabases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.DefaultDatabases is not null)
        {
            foreach (var pair in config.DefaultDatabases)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                normalizedDatabases[NormalizeClusterUrl(pair.Key)] = pair.Value;
            }
        }

        config.DefaultDatabases = normalizedDatabases;
        if (!string.IsNullOrWhiteSpace(config.DefaultClusterUrl))
        {
            config.DefaultClusterUrl = NormalizeClusterUrl(config.DefaultClusterUrl);
        }

        for (var i = 0; i < config.Clusters.Count; i++)
        {
            var current = config.Clusters[i];
            current.Name = current.Name.Trim();
            current.Url = NormalizeClusterUrl(current.Url);
        }

        var normalizedOverrides = new List<SchemaCacheOverride>();
        foreach (var current in config.SchemaCache.Overrides)
        {
            if (string.IsNullOrWhiteSpace(current.ClusterUrl) ||
                string.IsNullOrWhiteSpace(current.Database))
            {
                continue;
            }

            normalizedOverrides.Add(new SchemaCacheOverride
            {
                ClusterUrl = NormalizeClusterUrl(current.ClusterUrl),
                Database = current.Database.Trim(),
                TtlSeconds = current.TtlSeconds
            });
        }

        config.SchemaCache.Overrides = normalizedOverrides;

        return config;
    }
}
