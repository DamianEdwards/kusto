using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace Kusto.Cli.Tests;

public sealed class GitHubReleaseServiceTests
{
    [Fact]
    public void SelectLatestRelease_UsesSemVerInsteadOfApiOrder()
    {
        using var releases = JsonDocument.Parse("""
            [
              {
                "tag_name": "v1.1.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-win-x64.zip" }]
              },
              {
                "tag_name": "v1.10.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-win-x64.zip" }]
              },
              {
                "tag_name": "v1.2.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-win-x64.zip" }]
              }
            ]
            """);

        var selected = GitHubReleaseService.SelectLatestRelease(
            releases.RootElement,
            NuGetVersion.Parse("1.0.0"),
            allowPreRelease: false,
            stableOnly: false,
            "kusto-win-x64.zip");

        Assert.NotNull(selected);
        Assert.Equal("v1.10.0", selected.TagName);
    }

    [Fact]
    public void SelectLatestRelease_PreReleaseCanSelectStable()
    {
        using var releases = JsonDocument.Parse("""
            [
              {
                "tag_name": "v1.1.0-pre.2.rel",
                "draft": false,
                "prerelease": true,
                "assets": [{ "name": "kusto-linux-x64.tar.gz" }]
              },
              {
                "tag_name": "v1.1.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-linux-x64.tar.gz" }]
              }
            ]
            """);

        var selected = GitHubReleaseService.SelectLatestRelease(
            releases.RootElement,
            NuGetVersion.Parse("1.1.0-pre.1.rel"),
            allowPreRelease: false,
            stableOnly: false,
            "kusto-linux-x64.tar.gz");

        Assert.NotNull(selected);
        Assert.Equal("v1.1.0", selected.TagName);
    }

    [Fact]
    public void SelectLatestRelease_IgnoresDraftsSnapshotsAndMissingAssets()
    {
        using var releases = JsonDocument.Parse("""
            [
              {
                "tag_name": "v9.0.0",
                "draft": true,
                "prerelease": false,
                "assets": [{ "name": "kusto-osx-arm64.tar.gz" }]
              },
              {
                "tag_name": "install-scripts-v2026.08.31.1",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-osx-arm64.tar.gz" }]
              },
              {
                "tag_name": "v2.0.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "kusto-win-x64.zip" }]
              }
            ]
            """);

        var selected = GitHubReleaseService.SelectLatestRelease(
            releases.RootElement,
            NuGetVersion.Parse("1.0.0"),
            allowPreRelease: false,
            stableOnly: false,
            "kusto-osx-arm64.tar.gz");

        Assert.Null(selected);
    }

    [Fact]
    public void ExtractReleaseArchive_RejectsTarPathTraversal()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"kusto-tar-test-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(root, "payload.tar.gz");
        var destination = Path.Combine(root, "extract");
        var outsidePath = Path.Combine(root, "outside.txt");
        Directory.CreateDirectory(root);

        try
        {
            using (var archive = File.Create(archivePath))
            using (var gzip = new GZipStream(archive, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                var bytes = Encoding.UTF8.GetBytes("unexpected");
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../outside.txt")
                {
                    DataStream = new MemoryStream(bytes)
                });
            }

            var exception = Assert.Throws<UserFacingException>(
                () => GitHubReleaseService.ExtractReleaseArchive(
                    archivePath,
                    destination));

            Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
