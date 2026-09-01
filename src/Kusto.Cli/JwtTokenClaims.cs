using System.Text.Json;

namespace Kusto.Cli;

/// <summary>
/// Decodes and validates the non-secret claims of a JWT access token locally, without
/// ever logging or persisting the token itself. Only the <c>tid</c> (tenant) and
/// <c>aud</c> (audience/resource) claims are inspected to confirm the broker returned a
/// token bound to the expected account tenant and Kusto resource.
/// </summary>
internal static class JwtTokenClaims
{
    /// <summary>
    /// Validates that the token's <c>tid</c> matches <paramref name="expectedTenantId"/>
    /// and its <c>aud</c> matches <paramref name="expectedResource"/>. Throws a
    /// <see cref="UserFacingException"/> describing the mismatch on failure. The raw token
    /// is never included in any message.
    /// </summary>
    public static void Validate(string accessToken, string expectedTenantId, string expectedResource)
    {
        if (!TryReadPayload(accessToken, out var tenantId, out var audience))
        {
            throw new UserFacingException("The access token returned by the broker could not be validated.");
        }

        if (!string.Equals(tenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException(
                "The access token returned by the broker is bound to a different tenant than the cluster is configured for. Re-run the login for this cluster.");
        }

        if (!AudienceMatches(audience, expectedResource))
        {
            throw new UserFacingException(
                "The access token returned by the broker is not scoped to the Kusto resource. Re-run the login for this cluster.");
        }
    }

    private static bool TryReadPayload(string accessToken, out string? tenantId, out string? audience)
    {
        tenantId = null;
        audience = null;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = DecodeBase64Url(segments[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadBytes);
            var root = document.RootElement;
            if (root.TryGetProperty("tid", out var tidElement) && tidElement.ValueKind == JsonValueKind.String)
            {
                tenantId = tidElement.GetString();
            }

            if (root.TryGetProperty("aud", out var audElement))
            {
                audience = audElement.ValueKind switch
                {
                    JsonValueKind.String => audElement.GetString(),
                    JsonValueKind.Array when audElement.GetArrayLength() > 0 => audElement[0].GetString(),
                    _ => null
                };
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(audience);
    }

    private static bool AudienceMatches(string? audience, string expectedResource)
    {
        if (string.IsNullOrWhiteSpace(audience))
        {
            return false;
        }

        var normalizedAudience = audience.TrimEnd('/');
        var normalizedResource = expectedResource.TrimEnd('/');

        if (string.Equals(normalizedAudience, normalizedResource, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tolerate audience expressed as a bare host or with a differing scheme by
        // comparing hosts when both parse as absolute URIs.
        if (Uri.TryCreate(normalizedAudience, UriKind.Absolute, out var audienceUri) &&
            Uri.TryCreate(normalizedResource, UriKind.Absolute, out var resourceUri))
        {
            return string.Equals(audienceUri.Host, resourceUri.Host, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            case 1:
                throw new FormatException("Invalid base64url payload length.");
        }

        return Convert.FromBase64String(normalized);
    }
}
