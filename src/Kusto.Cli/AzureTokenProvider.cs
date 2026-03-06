using Azure.Core;
using Azure.Identity;

namespace Kusto.Cli;

public sealed class AzureTokenProvider : ITokenProvider
{
    private readonly Func<Uri, TokenCredential> _credentialFactory;

    public AzureTokenProvider()
        : this(CreateCredential)
    {
    }

    internal AzureTokenProvider(Func<Uri, TokenCredential> credentialFactory)
    {
        _credentialFactory = credentialFactory;
    }

    public async Task<string> GetTokenAsync(string clusterUrl, CancellationToken cancellationToken)
    {
        var cloud = KustoCloudEnvironmentResolver.ResolveForAuthentication(clusterUrl);
        var credential = _credentialFactory(cloud.AuthorityHost);
        var token = await credential.GetTokenAsync(new TokenRequestContext([cloud.Scope]), cancellationToken);
        return token.Token;
    }

    private static TokenCredential CreateCredential(Uri authorityHost)
    {
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            AuthorityHost = authorityHost
        });
    }
}
