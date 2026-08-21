namespace Kusto.Cli;

/// <summary>
/// Common validation used by both the silent query-time WAM token provider and the
/// interactive login/logout service. Produces consistent, actionable, WAM-appropriate
/// error messages that never reference <c>az login</c>.
/// </summary>
internal static class WamAuthGuards
{
    public static string Describe(ResolvedCluster cluster) =>
        string.IsNullOrWhiteSpace(cluster.Name) ? cluster.Url : cluster.Name!;

    public static void RequireWindows(IPlatform platform, ResolvedCluster cluster)
    {
        if (!platform.IsWindows)
        {
            throw new UserFacingException(
                $"WAM authentication for cluster '{Describe(cluster)}' is only available on Windows.");
        }
    }

    public static (string TenantId, string Account) RequireWamConfiguration(ResolvedCluster cluster)
    {
        if (!ClusterAuthenticationModes.IsWam(cluster.Authentication))
        {
            throw new UserFacingException(
                $"Cluster '{Describe(cluster)}' is not configured for WAM authentication.");
        }

        var tenantId = cluster.Authentication.TenantId?.Trim();
        var account = cluster.Authentication.Account?.Trim();

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(account))
        {
            throw new UserFacingException(
                $"Cluster '{Describe(cluster)}' is configured for WAM but is missing a tenant or account. " +
                $"Run 'kusto cluster login {Describe(cluster)} --tenant <tenantId> --account <user@domain>'.");
        }

        return (tenantId, account);
    }

    public static UserFacingException LoginRequired(ResolvedCluster cluster, Exception? inner = null) =>
        new($"No usable WAM sign-in was found for cluster '{Describe(cluster)}'. Run 'kusto cluster login {Describe(cluster)}' to sign in.", inner);
}
