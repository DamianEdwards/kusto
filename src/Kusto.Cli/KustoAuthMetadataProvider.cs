using System.Net;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

/// <summary>
/// Reads and validates a cluster's unauthenticated <c>/v1/rest/auth/metadata</c> document.
/// Only Azure Public Cloud <c>*.kusto.windows.net</c> HTTPS clusters are supported today,
/// and the advertised login endpoint and Kusto resource must match the expected public
/// cloud values. Requests are pinned to the cluster origin and never followed off-origin.
/// </summary>
public interface IKustoAuthMetadataProvider
{
    Task<KustoAuthMetadataResult> GetAsync(string clusterUrl, CancellationToken cancellationToken);
}

/// <summary>Public-facing, non-secret projection of validated cluster auth metadata.</summary>
public sealed class KustoAuthMetadataResult
{
    public required string ClientId { get; init; }
    public required string Resource { get; init; }
    public required string LoginEndpoint { get; init; }
    public required string LoginHost { get; init; }
    public required Uri AuthorityHost { get; init; }
}

internal sealed class KustoAuthMetadataProvider(HttpClient httpClient, ILogger logger) : IKustoAuthMetadataProvider
{
    private const string MetadataPath = "/v1/rest/auth/metadata";

    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger _logger = logger;

    public async Task<KustoAuthMetadataResult> GetAsync(string clusterUrl, CancellationToken cancellationToken)
    {
        var origin = ValidateSupportedCluster(clusterUrl);
        var metadataUri = new Uri(origin, MetadataPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
        using var response = await SendAsync(request, cancellationToken);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new UserFacingException(
                $"The cluster '{origin.Host}' attempted to redirect the authentication metadata request. WAM metadata must be served directly by the cluster.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UserFacingException(
                $"Failed to read authentication metadata from '{origin.Host}' (HTTP {(int)response.StatusCode}).");
        }

        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && !IsSameOrigin(origin, finalUri))
        {
            throw new UserFacingException(
                $"The authentication metadata request for '{origin.Host}' was redirected off-origin and was rejected.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return Parse(origin, body);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to reach the Kusto auth metadata endpoint at {Uri}.", request.RequestUri);
            throw new UserFacingException(
                $"Could not reach the authentication metadata endpoint for '{request.RequestUri?.Host}'. Verify the cluster URL and your network connectivity.",
                ex);
        }
    }

    private static Uri ValidateSupportedCluster(string clusterUrl)
    {
        if (!Uri.TryCreate(clusterUrl, UriKind.Absolute, out var uri))
        {
            throw new UserFacingException($"'{clusterUrl}' is not a valid cluster URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UserFacingException("WAM authentication requires an HTTPS cluster URL.");
        }

        if (!uri.Host.EndsWith(WamConstants.PublicClusterHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"WAM authentication currently supports only Azure Public Cloud clusters ('*{WamConstants.PublicClusterHostSuffix}'). '{uri.Host}' is not supported.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static bool IsSameOrigin(Uri expected, Uri actual) =>
        string.Equals(expected.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port;

    private static KustoAuthMetadataResult Parse(Uri origin, string body)
    {
        KustoAuthMetadataEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(body, KustoJsonSerializerContext.Default.KustoAuthMetadataEnvelope);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"The authentication metadata from '{origin.Host}' was not valid JSON.", ex);
        }

        var azureAd = envelope?.AzureAd
            ?? throw new UserFacingException($"The authentication metadata from '{origin.Host}' did not contain Azure AD settings.");

        var loginEndpoint = (azureAd.LoginEndpoint ?? string.Empty).Trim();
        if (!string.Equals(loginEndpoint.TrimEnd('/'), WamConstants.ExpectedLoginEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The cluster '{origin.Host}' advertised an unsupported login endpoint. Only '{WamConstants.ExpectedLoginEndpoint}' is supported for WAM.");
        }

        var resource = (azureAd.KustoServiceResourceId ?? string.Empty).Trim();
        if (!string.Equals(resource.TrimEnd('/'), WamConstants.ExpectedResource, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                $"The cluster '{origin.Host}' advertised an unsupported Kusto resource. Only '{WamConstants.ExpectedResource}' is supported for WAM.");
        }

        var clientId = (azureAd.KustoClientAppId ?? string.Empty).Trim();
        if (!Guid.TryParse(clientId, out _))
        {
            throw new UserFacingException(
                $"The cluster '{origin.Host}' advertised an invalid client application id in its authentication metadata.");
        }

        return new KustoAuthMetadataResult
        {
            ClientId = clientId,
            Resource = WamConstants.ExpectedResource,
            LoginEndpoint = WamConstants.ExpectedLoginEndpoint,
            LoginHost = new Uri(WamConstants.ExpectedLoginEndpoint).Host,
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
        };
    }
}
