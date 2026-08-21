using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

/// <summary>
/// Performs explicit, interactive WAM broker authentication for a cluster (login) and
/// deletes the stored authentication record (logout). Login is Windows-only and requires a
/// usable parent window handle. The returned broker <see cref="AuthenticationRecord"/> is
/// strictly validated against the configured account/tenant before it is persisted, and the
/// warm-up token's claims are validated locally. Never falls back to <c>DefaultAzureCredential</c>.
/// </summary>
internal sealed class WamClusterAuthenticator(
    IPlatform platform,
    IKustoAuthMetadataProvider metadataProvider,
    IAuthenticationRecordStore recordStore,
    IWamBrokerCredentialFactory credentialFactory,
    IWindowHandleProvider windowHandleProvider,
    string tokenCacheName,
    ILogger logger) : IClusterAuthenticationService
{
    private readonly IPlatform _platform = platform;
    private readonly IKustoAuthMetadataProvider _metadataProvider = metadataProvider;
    private readonly IAuthenticationRecordStore _recordStore = recordStore;
    private readonly IWamBrokerCredentialFactory _credentialFactory = credentialFactory;
    private readonly IWindowHandleProvider _windowHandleProvider = windowHandleProvider;
    private readonly string _tokenCacheName = tokenCacheName;
    private readonly ILogger _logger = logger;

    public async Task LoginAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
    {
        WamAuthGuards.RequireWindows(_platform, cluster);
        var (tenantId, account) = WamAuthGuards.RequireWamConfiguration(cluster);

        var metadata = await _metadataProvider.GetAsync(cluster.Url, cancellationToken);
        var key = AuthenticationRecordKey.Create(
            cluster.Url,
            tenantId,
            account,
            metadata.ClientId,
            metadata.Resource,
            metadata.LoginHost);

        var parentWindowHandle = _windowHandleProvider.GetParentWindowHandle();
        if (parentWindowHandle == IntPtr.Zero)
        {
            throw new UserFacingException(
                "Could not obtain a window handle to anchor the sign-in dialog. Run 'kusto cluster login' from an interactive console session.");
        }

        var request = new WamCredentialRequest
        {
            ClientId = metadata.ClientId,
            TenantId = tenantId,
            Account = account,
            AuthorityHost = metadata.AuthorityHost,
            TokenCacheName = _tokenCacheName,
            ParentWindowHandle = parentWindowHandle,
            DisableAutomaticAuthentication = false,
            AuthenticationRecord = null
        };

        var credential = _credentialFactory.Create(request);
        var context = new TokenRequestContext([WamConstants.BuildScope(metadata.Resource)], tenantId: tenantId);

        AuthenticationRecord record;
        try
        {
            record = await credential.AuthenticateAsync(context, cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogDebug(ex, "Interactive WAM authentication failed for {Cluster}.", WamAuthGuards.Describe(cluster));
            throw new UserFacingException(
                $"Sign-in for cluster '{WamAuthGuards.Describe(cluster)}' did not complete. Try again and complete the Windows sign-in prompt.",
                ex);
        }

        ValidateReturnedRecord(cluster, record, tenantId, account, metadata);

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(context, cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new UserFacingException(
                $"Sign-in for cluster '{WamAuthGuards.Describe(cluster)}' completed but a token could not be acquired. Try the login again.",
                ex);
        }

        JwtTokenClaims.Validate(token.Token, tenantId, metadata.Resource);
        await _recordStore.SaveAsync(key, record, cancellationToken);
    }

    public async Task<bool> LogoutAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
    {
        var (tenantId, account) = WamAuthGuards.RequireWamConfiguration(cluster);
        return await _recordStore.DeleteAsync(
            cluster.Url,
            tenantId,
            account,
            cancellationToken);
    }

    private static void ValidateReturnedRecord(
        ResolvedCluster cluster,
        AuthenticationRecord record,
        string tenantId,
        string account,
        KustoAuthMetadataResult metadata)
    {
        if (!string.Equals(record.Username?.Trim(), account, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The account you signed in with ('{record.Username}') does not match the configured account ('{account}') for cluster '{WamAuthGuards.Describe(cluster)}'. Sign in with the configured account or update it with '--account'.");
        }

        if (!string.Equals(record.TenantId?.Trim(), tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The tenant you signed in to ('{record.TenantId}') does not match the configured tenant ('{tenantId}') for cluster '{WamAuthGuards.Describe(cluster)}'.");
        }

        if (!string.Equals(record.ClientId?.Trim(), metadata.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The broker returned an unexpected client id for cluster '{WamAuthGuards.Describe(cluster)}'.");
        }

        if (!string.IsNullOrWhiteSpace(record.Authority) &&
            !string.Equals(record.Authority.Trim(), metadata.LoginHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The broker returned an unexpected sign-in authority for cluster '{WamAuthGuards.Describe(cluster)}'.");
        }
    }
}
