using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

/// <summary>
/// Query-time token provider for WAM-configured clusters. Token acquisition is strictly
/// silent: it uses the broker with <c>DisableAutomaticAuthentication = true</c> and a
/// previously stored <see cref="AuthenticationRecord"/>. Any missing or stale state fails
/// with an actionable <c>kusto cluster login</c> message and never shows UI. This provider
/// never falls back to <c>DefaultAzureCredential</c>.
/// </summary>
internal sealed class WamTokenProvider(
    IPlatform platform,
    IKustoAuthMetadataProvider metadataProvider,
    IAuthenticationRecordStore recordStore,
    IWamBrokerCredentialFactory credentialFactory,
    IWindowHandleProvider windowHandleProvider,
    string tokenCacheName,
    ILogger logger) : ITokenProvider
{
    private readonly IPlatform _platform = platform;
    private readonly IKustoAuthMetadataProvider _metadataProvider = metadataProvider;
    private readonly IAuthenticationRecordStore _recordStore = recordStore;
    private readonly IWamBrokerCredentialFactory _credentialFactory = credentialFactory;
    private readonly IWindowHandleProvider _windowHandleProvider = windowHandleProvider;
    private readonly string _tokenCacheName = tokenCacheName;
    private readonly ILogger _logger = logger;

    public async Task<string> GetTokenAsync(ResolvedCluster cluster, CancellationToken cancellationToken)
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

        var record = await _recordStore.TryLoadAsync(key, cancellationToken)
            ?? throw WamAuthGuards.LoginRequired(cluster);

        var request = new WamCredentialRequest
        {
            ClientId = metadata.ClientId,
            TenantId = tenantId,
            Account = account,
            AuthorityHost = metadata.AuthorityHost,
            TokenCacheName = _tokenCacheName,
            ParentWindowHandle = _windowHandleProvider.GetParentWindowHandle(),
            DisableAutomaticAuthentication = true,
            AuthenticationRecord = record
        };

        var credential = _credentialFactory.Create(request);
        var context = new TokenRequestContext([WamConstants.BuildScope(metadata.Resource)], tenantId: tenantId);

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(context, cancellationToken);
        }
        catch (AuthenticationRequiredException ex)
        {
            throw WamAuthGuards.LoginRequired(cluster, ex);
        }
        catch (CredentialUnavailableException ex)
        {
            throw WamAuthGuards.LoginRequired(cluster, ex);
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogDebug(ex, "Silent WAM token acquisition failed for {Cluster}.", WamAuthGuards.Describe(cluster));
            throw new UserFacingException(
                $"WAM authentication failed for cluster '{WamAuthGuards.Describe(cluster)}'. Run 'kusto cluster login {WamAuthGuards.Describe(cluster)}' to sign in again.",
                ex);
        }

        JwtTokenClaims.Validate(token.Token, tenantId, metadata.Resource);
        return token.Token;
    }
}
