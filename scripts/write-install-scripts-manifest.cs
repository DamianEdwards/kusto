#!/usr/bin/env dotnet

#:package System.CommandLine@2.0.3
#:property PublishAot=false

using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

var directoryOption = new Option<string>("--directory") { Required = true };
var manifestVersionOption = new Option<string>("--manifest-version") { Required = true };
var sourceCommitOption = new Option<string>("--source-commit") { Required = true };
var sourceRefOption = new Option<string>("--source-ref");
sourceRefOption.DefaultValueFactory = _ => "refs/heads/main";

var command = new RootCommand("Generate install-script checksums and manifest metadata.");
command.Options.Add(directoryOption);
command.Options.Add(manifestVersionOption);
command.Options.Add(sourceCommitOption);
command.Options.Add(sourceRefOption);
command.SetAction(parseResult => ExecuteHandled(() =>
{
    var directory = Path.GetFullPath(parseResult.GetValue(directoryOption)!);
    var manifestVersion = parseResult.GetValue(manifestVersionOption)!;
    var sourceCommit = parseResult.GetValue(sourceCommitOption)!;
    var sourceRef = parseResult.GetValue(sourceRefOption)!;
    var scriptNames = new[] { "install.ps1", "install.sh" };
    var scripts = scriptNames.Select(name =>
    {
        var path = Path.Combine(directory, name);
        EnsureFileExists(path, "Install script");
        return new InstallScriptEntry(name, ComputeSha256(path));
    }).ToArray();

    File.WriteAllLines(
        Path.Combine(directory, "checksums.txt"),
        scripts.Select(script => $"{script.Sha256}  {script.Name}"));
    var manifest = new InstallScriptsManifest(
        manifestVersion,
        sourceCommit,
        sourceRef,
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        scripts);
    File.WriteAllText(
        Path.Combine(directory, "install-scripts.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
}));

return command.Parse(args).Invoke();

static void ExecuteHandled(Action action)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException or JsonException)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.Exit(1);
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

internal sealed record InstallScriptsManifest(
    string Version,
    string SourceCommit,
    string SourceRef,
    string PublishedAt,
    IReadOnlyList<InstallScriptEntry> Scripts);
internal sealed record InstallScriptEntry(string Name, string Sha256);
