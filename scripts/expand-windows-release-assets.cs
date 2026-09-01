#!/usr/bin/env dotnet

#:package System.CommandLine@2.0.3
#:property PublishAot=false

using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

var bundleDirectoryOption = new Option<string>("--bundle-directory") { Required = true };
var workingDirectoryOption = new Option<string>("--working-directory") { Required = true };
var requiredPayloadFiles = new[]
{
    "kusto.exe",
    "libSkiaSharp.dll",
    "libHarfBuzzSharp.dll",
    "libsodium.dll",
    "LICENSE",
    "THIRD-PARTY-NOTICES.md",
    "payload-manifest.json"
};
var command = new RootCommand("Expand kusto Windows archives and emit a staging manifest.");
command.Options.Add(bundleDirectoryOption);
command.Options.Add(workingDirectoryOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var bundleDirectory = Path.GetFullPath(parseResult.GetValue(bundleDirectoryOption)!);
    var workingDirectory = Path.GetFullPath(parseResult.GetValue(workingDirectoryOption)!);
    var metadataPath = Path.Combine(bundleDirectory, "release-metadata.json");
    EnsureDirectoryExists(bundleDirectory, "Bundle directory");
    EnsureFileExists(metadataPath, "Release metadata file");

    var metadata = JsonSerializer.Deserialize(
        File.ReadAllText(metadataPath),
        WindowsAssetsJsonContext.Default.ReleaseMetadataDocument)
        ?? throw new InvalidOperationException($"Release metadata file '{metadataPath}' could not be parsed.");

    var windowsAssets = metadata.WindowsAssets.Count > 0
        ? metadata.WindowsAssets
        : metadata.Assets.Where(asset => asset.Platform == "win").ToArray();
    if (windowsAssets.Count == 0)
    {
        throw new InvalidOperationException($"No Windows assets were found in '{metadataPath}'.");
    }

    Directory.CreateDirectory(workingDirectory);
    var manifestEntries = new List<WindowsAssetManifestEntry>();
    foreach (var asset in windowsAssets)
    {
        var archivePath = Path.Combine(bundleDirectory, asset.Name);
        EnsureFileExists(archivePath, "Archive");
        var assetDirectory = Path.Combine(workingDirectory, asset.RuntimeIdentifier);
        if (Directory.Exists(assetDirectory))
        {
            Directory.Delete(assetDirectory, recursive: true);
        }

        Directory.CreateDirectory(assetDirectory);
        ZipFile.ExtractToDirectory(archivePath, assetDirectory, overwriteFiles: true);
        foreach (var requiredName in requiredPayloadFiles)
        {
            EnsureFileExists(Path.Combine(assetDirectory, requiredName), "Expanded Windows asset");
        }
        EnsurePayloadManifestMatches(assetDirectory);

        manifestEntries.Add(new WindowsAssetManifestEntry(asset.Name, asset.RuntimeIdentifier, assetDirectory));
    }

    File.WriteAllText(
        Path.Combine(workingDirectory, "windows-assets-manifest.json"),
        JsonSerializer.Serialize(manifestEntries, WindowsAssetsJsonContext.Default.ListWindowsAssetManifestEntry));
}));

return command.Parse(args).Invoke();

static void ExecuteHandled(Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or FileNotFoundException
        or InvalidOperationException or IOException or JsonException)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
    }
}

static void EnsureDirectoryExists(string path, string description)
{
    if (!Directory.Exists(path))
    {
        throw new DirectoryNotFoundException($"{description} '{path}' was not found.");
    }
}

static void EnsureFileExists(string path, string description)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"{description} '{path}' was not found.");
    }
}

static void EnsurePayloadManifestMatches(string directory)
{
    const string manifestName = "payload-manifest.json";
    var manifestPath = Path.Combine(directory, manifestName);
    var manifest = JsonSerializer.Deserialize(
        File.ReadAllText(manifestPath),
        WindowsAssetsJsonContext.Default.PayloadManifest)
        ?? throw new InvalidOperationException($"Payload manifest '{manifestPath}' could not be parsed.");
    if (manifest.Files.Count == 0)
    {
        throw new InvalidOperationException($"Payload manifest '{manifestPath}' did not declare any files.");
    }

    var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string? previousEntry = null;
    foreach (var entry in manifest.Files)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) || entry.Contains('\\'))
        {
            throw new InvalidOperationException($"Payload manifest contains invalid path '{entry}'.");
        }
        if (previousEntry is not null && StringComparer.Ordinal.Compare(previousEntry, entry) > 0)
        {
            throw new InvalidOperationException("Payload manifest paths are not sorted using ordinal comparison.");
        }
        previousEntry = entry;
        var normalized = entry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(directory, normalized));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Payload manifest path '{entry}' is outside the payload.");
        }
        var relativePath = Path.GetRelativePath(directory, fullPath);
        if (!entry.Equals(relativePath.Replace('\\', '/'), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Payload manifest path '{entry}' is not normalized.");
        }
        if (!declared.Add(relativePath))
        {
            throw new InvalidOperationException($"Payload manifest contains duplicate path '{entry}'.");
        }
        EnsureFileExists(fullPath, "Payload manifest entry");
    }

    var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(directory, path))
        .Where(path => !path.Equals(manifestName, StringComparison.OrdinalIgnoreCase))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!declared.SetEquals(actual))
    {
        throw new InvalidOperationException(
            $"Payload manifest '{manifestPath}' does not exactly describe the payload; the manifest itself must be excluded.");
    }
}

internal sealed record ReleaseMetadataDocument(
    string Version,
    string? SourceCommit,
    IReadOnlyList<ReleaseAsset> Assets,
    IReadOnlyList<ReleaseAsset> WindowsAssets);
internal sealed record ReleaseAsset(
    string Name,
    string RuntimeIdentifier,
    string Platform,
    string Architecture,
    string FileType,
    string CommandName,
    string Sha256);
internal sealed record WindowsAssetManifestEntry(string AssetName, string RuntimeIdentifier, string StagingDirectory);
internal sealed record PayloadManifest(IReadOnlyList<string> Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseMetadataDocument))]
[JsonSerializable(typeof(List<WindowsAssetManifestEntry>))]
[JsonSerializable(typeof(PayloadManifest))]
internal sealed partial class WindowsAssetsJsonContext : JsonSerializerContext;
