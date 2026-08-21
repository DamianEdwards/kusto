namespace Kusto.Cli;

/// <summary>
/// Routes token acquisition to the correct provider based on the resolved cluster's
/// authentication mode. WAM-configured clusters go to the WAM provider; everything else
/// uses the default (<c>DefaultAzureCredential</c>) provider. The WAM provider never falls
/// back to the default provider: its failures propagate so a misconfigured or unauthenticated
/// WAM cluster produces an actionable WAM error rather than a silent default-credential attempt.
/// </summary>
public sealed class RoutingTokenProvider(ITokenProvider defaultProvider, ITokenProvider wamProvider) : ITokenProvider
{
    private readonly ITokenProvider _defaultProvider = defaultProvider;
    private readonly ITokenProvider _wamProvider = wamProvider;

    public Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
    {
        if (ClusterAuthenticationModes.IsWam(cluster.Authentication))
        {
            return _wamProvider.GetTokenAsync(cluster, cancellationToken);
        }

        if (ClusterAuthenticationModes.IsDefault(cluster.Authentication))
        {
            return _defaultProvider.GetTokenAsync(cluster, cancellationToken);
        }

        throw new UserFacingException(
            $"Cluster '{cluster.Name ?? cluster.Url}' has unsupported authentication mode '{cluster.Authentication.Mode}'.");
    }
}
