namespace Kusto.Cli;

/// <summary>
/// Fixed, non-secret constants for the Azure Public Cloud WAM authentication path.
/// The current implementation intentionally supports only Azure Public Cloud
/// <c>*.kusto.windows.net</c> clusters; sovereign clouds are out of scope for now.
/// </summary>
internal static class WamConstants
{
    /// <summary>Only Azure Public Cloud Kusto endpoints are supported for WAM today.</summary>
    public const string PublicClusterHostSuffix = ".kusto.windows.net";

    /// <summary>The only login endpoint accepted from cluster auth metadata.</summary>
    public const string ExpectedLoginEndpoint = "https://login.microsoftonline.com";

    /// <summary>The only Kusto service resource accepted from cluster auth metadata.</summary>
    public const string ExpectedResource = "https://kusto.kusto.windows.net";

    /// <summary>Scope suffix appended to the resource to request a token via the broker.</summary>
    public const string DefaultScopeSuffix = "/.default";

    public static string BuildScope(string resource) =>
        $"{resource.TrimEnd('/')}{DefaultScopeSuffix}";
}
