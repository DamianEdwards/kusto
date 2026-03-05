using Azure.Core;
using Azure.Identity;

namespace Kusto.Cli;

public sealed class AzureTokenProvider : ITokenProvider
{
    private static readonly TokenRequestContext RequestContext = new(["https://kusto.kusto.windows.net/.default"]);
    private readonly DefaultAzureCredential _credential = new();

    public async Task<string> GetTokenAsync(string clusterUrl, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(RequestContext, cancellationToken);
        return token.Token;
    }
}
