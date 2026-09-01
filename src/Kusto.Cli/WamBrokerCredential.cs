using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;

namespace Kusto.Cli;

/// <summary>
/// Parameters used to construct a WAM broker credential. Carries only non-secret
/// coordinates plus an optional previously-established <see cref="AuthenticationRecord"/>.
/// </summary>
internal sealed class WamCredentialRequest
{
    public required string ClientId { get; init; }
    public required string TenantId { get; init; }
    public required string Account { get; init; }
    public required Uri AuthorityHost { get; init; }
    public required string TokenCacheName { get; init; }
    public required IntPtr ParentWindowHandle { get; init; }
    public bool DisableAutomaticAuthentication { get; init; }
    public AuthenticationRecord? AuthenticationRecord { get; init; }
}

/// <summary>
/// Minimal seam over the concrete <see cref="InteractiveBrowserCredential"/> broker so the
/// login/silent flows can be unit-tested without invoking the real WAM broker or opening UI.
/// </summary>
internal interface IWamBrokerCredential
{
    Task<AuthenticationRecord> AuthenticateAsync(TokenRequestContext context, CancellationToken cancellationToken);
    ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken);
}

internal interface IWamBrokerCredentialFactory
{
    IWamBrokerCredential Create(WamCredentialRequest request);
}

/// <summary>
/// Real broker factory. Builds an <see cref="InteractiveBrowserCredential"/> configured with
/// <see cref="InteractiveBrowserCredentialBrokerOptions"/>, persistent token cache, and (for
/// silent acquisition) <c>DisableAutomaticAuthentication = true</c> plus the stored
/// <see cref="AuthenticationRecord"/> so token acquisition never shows UI.
/// </summary>
internal sealed class WamBrokerCredentialFactory : IWamBrokerCredentialFactory
{
    public IWamBrokerCredential Create(WamCredentialRequest request)
    {
        var options = new InteractiveBrowserCredentialBrokerOptions(request.ParentWindowHandle)
        {
            ClientId = request.ClientId,
            TenantId = request.TenantId,
            LoginHint = request.Account,
            AuthorityHost = request.AuthorityHost,
            DisableAutomaticAuthentication = request.DisableAutomaticAuthentication,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = request.TokenCacheName
            },
            AuthenticationRecord = request.AuthenticationRecord
        };

        return new WamBrokerCredential(new InteractiveBrowserCredential(options));
    }

    private sealed class WamBrokerCredential(InteractiveBrowserCredential credential) : IWamBrokerCredential
    {
        private readonly InteractiveBrowserCredential _credential = credential;

        public Task<AuthenticationRecord> AuthenticateAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            _credential.AuthenticateAsync(context, cancellationToken);

        public ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            _credential.GetTokenAsync(context, cancellationToken);
    }
}
