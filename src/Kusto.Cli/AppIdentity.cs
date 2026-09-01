namespace Kusto.Cli;

internal static class AppIdentity
{
    public const string ProductName = "Kusto CLI";
    public const string CommandName = "kusto";
    public const string DefaultRepository = "DamianEdwards/kusto-cli";
    public const string HomeEnvVar = "KUSTO_HOME";
    public const string DisableSelfUpdatesEnvVar = "KUSTO_DISABLE_SELF_UPDATES";
    public const string UpdateSourceEnvVar = "KUSTO_UPDATE_SOURCE";
    public const string UpdateRepositoryEnvVar = "KUSTO_UPDATE_REPOSITORY";

    public static string GetExecutableFileName()
        => OperatingSystem.IsWindows() ? $"{CommandName}.exe" : CommandName;

    public static IReadOnlyList<string> GetExecutablePayloadFileNames()
        => OperatingSystem.IsWindows()
            ? ["kusto.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "libsodium.dll"]
            : OperatingSystem.IsMacOS()
                ? ["kusto", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libsodium.dylib"]
                : ["kusto", "libSkiaSharp.so", "libHarfBuzzSharp.so", "libsodium.so"];
}
