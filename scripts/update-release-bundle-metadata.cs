#!/usr/bin/env dotnet

#:package System.CommandLine@2.0.3
#:property PublishAot=false

using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var bundleDirectoryOption = new Option<string>("--bundle-directory") { Required = true };
var command = new RootCommand("Regenerate kusto release bundle checksums and metadata.");
command.Options.Add(bundleDirectoryOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var bundleDirectory = Path.GetFullPath(parseResult.GetValue(bundleDirectoryOption)!);
    var metadataPath = Path.Combine(bundleDirectory, "release-metadata.json");
    EnsureDirectoryExists(bundleDirectory, "Bundle directory");
    EnsureFileExists(metadataPath, "Release metadata file");

    var metadata = JsonSerializer.Deserialize(
        File.ReadAllText(metadataPath),
        BundleMetadataJsonContext.Default.ReleaseMetadataDocument)
        ?? throw new InvalidOperationException($"Release metadata file '{metadataPath}' could not be parsed.");

    if (string.IsNullOrWhiteSpace(metadata.Version))
    {
        throw new InvalidOperationException($"Release metadata in '{metadataPath}' did not contain a version.");
    }

    var updatedAssets = metadata.Assets
        .Select(asset =>
        {
            var assetPath = Path.Combine(bundleDirectory, asset.Name);
            EnsureFileExists(assetPath, "Release asset");
            return asset with { Sha256 = ComputeSha256(assetPath) };
        })
        .OrderBy(asset => asset.Name, StringComparer.Ordinal)
        .ToArray();

    File.WriteAllLines(
        Path.Combine(bundleDirectory, "checksums.txt"),
        updatedAssets.Select(asset => $"{asset.Sha256}  {asset.Name}"));

    var updatedMetadata = new ReleaseMetadataDocument(
        metadata.Version,
        metadata.SourceCommit,
        updatedAssets,
        updatedAssets.Where(asset => asset.Platform == "win").ToArray());
    File.WriteAllText(
        metadataPath,
        JsonSerializer.Serialize(updatedMetadata, BundleMetadataJsonContext.Default.ReleaseMetadataDocument));
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

static string ComputeSha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ReleaseMetadataDocument))]
internal sealed partial class BundleMetadataJsonContext : JsonSerializerContext;
