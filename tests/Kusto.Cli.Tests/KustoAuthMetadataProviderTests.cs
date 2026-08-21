using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class KustoAuthMetadataProviderTests
{
    private const string ValidBody =
        """
        { "AzureAD": { "LoginEndpoint": "https://login.microsoftonline.com", "KustoClientAppId": "db662dc1-0cfe-4e1c-a843-19a68e65be58", "KustoServiceResourceId": "https://kusto.kusto.windows.net" } }
        """;

    private static KustoAuthMetadataProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)), NullLogger.Instance);

    private static HttpResponseMessage Ok(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
            RequestMessage = request
        };

    [Fact]
    public async Task GetAsync_ValidMetadata_ReturnsValidatedResult()
    {
        var provider = CreateProvider(request => Ok(request, ValidBody));

        var result = await provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None);

        Assert.Equal("db662dc1-0cfe-4e1c-a843-19a68e65be58", result.ClientId);
        Assert.Equal(WamConstants.ExpectedResource, result.Resource);
        Assert.Equal(WamConstants.ExpectedLoginEndpoint, result.LoginEndpoint);
        Assert.Equal("login.microsoftonline.com", result.LoginHost);
    }

    [Fact]
    public async Task GetAsync_RequestsMetadataPathOnClusterOrigin()
    {
        Uri? requested = null;
        var provider = CreateProvider(request =>
        {
            requested = request.RequestUri;
            return Ok(request, ValidBody);
        });

        await provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None);

        Assert.Equal("https://cross-tenant.eastus2.kusto.windows.net/v1/rest/auth/metadata", requested!.AbsoluteUri);
    }

    [Fact]
    public async Task GetAsync_NonHttpsCluster_Throws()
    {
        var provider = CreateProvider(request => Ok(request, ValidBody));
        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync("http://cross-tenant.eastus2.kusto.windows.net", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_NonPublicCloudHost_Throws()
    {
        var provider = CreateProvider(request => Ok(request, ValidBody));
        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync("https://mycluster.kusto.chinacloudapi.cn", CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_WrongLoginEndpoint_Throws()
    {
        const string body =
            """
            { "AzureAD": { "LoginEndpoint": "https://login.contoso.example", "KustoClientAppId": "db662dc1-0cfe-4e1c-a843-19a68e65be58", "KustoServiceResourceId": "https://kusto.kusto.windows.net" } }
            """;
        var provider = CreateProvider(request => Ok(request, body));

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_WrongResource_Throws()
    {
        const string body =
            """
            { "AzureAD": { "LoginEndpoint": "https://login.microsoftonline.com", "KustoClientAppId": "db662dc1-0cfe-4e1c-a843-19a68e65be58", "KustoServiceResourceId": "https://graph.microsoft.com" } }
            """;
        var provider = CreateProvider(request => Ok(request, body));

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_InvalidClientId_Throws()
    {
        const string body =
            """
            { "AzureAD": { "LoginEndpoint": "https://login.microsoftonline.com", "KustoClientAppId": "not-a-guid", "KustoServiceResourceId": "https://kusto.kusto.windows.net" } }
            """;
        var provider = CreateProvider(request => Ok(request, body));

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_MissingAzureAd_Throws()
    {
        var provider = CreateProvider(request => Ok(request, "{ }"));
        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_RedirectStatus_Throws()
    {
        var provider = CreateProvider(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri("https://evil.example.com/v1/rest/auth/metadata");
            return response;
        });

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_OffOriginFinalUri_Throws()
    {
        var provider = CreateProvider(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidBody),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.com/v1/rest/auth/metadata")
            });

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_ServerError_Throws()
    {
        var provider = CreateProvider(request =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });

        await Assert.ThrowsAsync<UserFacingException>(() =>
            provider.GetAsync(WamTestSupport.ClusterUrl, CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
