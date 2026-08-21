namespace Kusto.Cli;

/// <summary>
/// Parses and validates the per-cluster authentication arguments (<c>--auth</c>,
/// <c>--tenant</c>, <c>--account</c>) used by <c>cluster add</c> and <c>cluster login</c>.
/// Produces a normalized <see cref="ClusterAuthentication"/> (or <c>null</c> for the default
/// flow). WAM requires both a GUID tenant and a UPN-style account.
/// </summary>
internal static class ClusterAuthenticationParser
{
    /// <summary>
    /// Parses the arguments for <c>cluster add</c>. Returns <c>null</c> for the default flow.
    /// </summary>
    public static ClusterAuthentication? ParseForAdd(string? authMode, string? tenant, string? account)
    {
        var mode = NormalizeMode(authMode);
        tenant = Trim(tenant);
        account = Trim(account);

        switch (mode)
        {
            case ClusterAuthenticationModes.Default:
                if (tenant is not null || account is not null)
                {
                    throw new UserFacingException("'--tenant' and '--account' are only valid with '--auth wam'.");
                }

                return null;

            case ClusterAuthenticationModes.Wam:
                if (tenant is null || account is null)
                {
                    throw new UserFacingException("WAM authentication requires both '--tenant' and '--account'.");
                }

                ValidateTenant(tenant);
                ValidateAccount(account);
                return new ClusterAuthentication
                {
                    Mode = ClusterAuthenticationModes.Wam,
                    TenantId = tenant,
                    Account = account
                };

            default:
                throw new UserFacingException(
                    $"Unknown authentication mode '{authMode}'. Valid values are 'default' or 'wam'.");
        }
    }

    /// <summary>
    /// Resolves the effective WAM authentication for <c>cluster login</c>, layering any
    /// provided <c>--tenant</c>/<c>--account</c> overrides on top of the cluster's existing
    /// configuration. The result is always a fully-populated WAM descriptor.
    /// </summary>
    public static ClusterAuthentication ResolveForLogin(ClusterAuthentication? existing, string? tenant, string? account)
    {
        tenant = Trim(tenant);
        account = Trim(account);

        var existingIsWam = ClusterAuthenticationModes.IsWam(existing);
        var effectiveTenant = tenant ?? (existingIsWam ? Trim(existing!.TenantId) : null);
        var effectiveAccount = account ?? (existingIsWam ? Trim(existing!.Account) : null);

        if (effectiveTenant is null || effectiveAccount is null)
        {
            throw new UserFacingException(
                "WAM login requires a tenant and account. Provide them with '--tenant <tenantId> --account <user@domain>'; they are saved for future logins.");
        }

        ValidateTenant(effectiveTenant);
        ValidateAccount(effectiveAccount);
        return new ClusterAuthentication
        {
            Mode = ClusterAuthenticationModes.Wam,
            TenantId = effectiveTenant,
            Account = effectiveAccount
        };
    }

    private static string NormalizeMode(string? authMode) =>
        string.IsNullOrWhiteSpace(authMode) ? ClusterAuthenticationModes.Default : authMode.Trim().ToLowerInvariant();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateTenant(string tenant)
    {
        if (!Guid.TryParse(tenant, out _))
        {
            throw new UserFacingException(
                $"'--tenant' must be a tenant GUID (for example '00000000-0000-0000-0000-000000000000'). '{tenant}' is not a valid GUID.");
        }
    }

    private static void ValidateAccount(string account)
    {
        var atIndex = account.IndexOf('@');
        if (atIndex <= 0 || atIndex >= account.Length - 1 || account.IndexOf('@', atIndex + 1) >= 0)
        {
            throw new UserFacingException(
                $"'--account' must be a user principal name in the form 'user@domain'. '{account}' is not valid.");
        }
    }
}
