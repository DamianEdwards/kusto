using Azure.Core;
using Azure.Identity;

namespace Kusto.Cli.Tests;

public sealed class AzureTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsync_UsGovernmentCluster_UsesGovernmentAuthorityAndScope()
    {
        Uri? authorityHost = null;
        TokenRequestContext? requestContext = null;
        var provider = new AzureTokenProvider(authority =>
        {
            authorityHost = authority;
            return new RecordingTokenCredential(context =>
            {
                requestContext = context;
                return new AccessToken("gov-token", DateTimeOffset.UtcNow.AddMinutes(5));
            });
        });

        var token = await provider.GetTokenAsync("https://mycluster.kusto.usgovcloudapi.net", CancellationToken.None);

        Assert.Equal("gov-token", token);
        Assert.Equal(AzureAuthorityHosts.AzureGovernment.AbsoluteUri, authorityHost?.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.usgovcloudapi.net/.default", Assert.Single(requestContext!.Value.Scopes));
    }

    [Fact]
    public async Task GetTokenAsync_UnknownCluster_FallsBackToPublicCloud()
    {
        Uri? authorityHost = null;
        TokenRequestContext? requestContext = null;
        var provider = new AzureTokenProvider(authority =>
        {
            authorityHost = authority;
            return new RecordingTokenCredential(context =>
            {
                requestContext = context;
                return new AccessToken("public-token", DateTimeOffset.UtcNow.AddMinutes(5));
            });
        });

        var token = await provider.GetTokenAsync("https://example.com", CancellationToken.None);

        Assert.Equal("public-token", token);
        Assert.Equal(AzureAuthorityHosts.AzurePublicCloud.AbsoluteUri, authorityHost?.AbsoluteUri);
        Assert.Equal("https://kusto.kusto.windows.net/.default", Assert.Single(requestContext!.Value.Scopes));
    }

    private sealed class RecordingTokenCredential(Func<TokenRequestContext, AccessToken> tokenFactory) : TokenCredential
    {
        private readonly Func<TokenRequestContext, AccessToken> _tokenFactory = tokenFactory;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return _tokenFactory(requestContext);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new ValueTask<AccessToken>(_tokenFactory(requestContext));
        }
    }
}
