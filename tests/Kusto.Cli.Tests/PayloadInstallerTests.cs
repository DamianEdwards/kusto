using System.Text.Json;

namespace Kusto.Cli.Tests;

public sealed class PayloadInstallerTests
{
    [Fact]
    public void ValidateManifest_AcceptsExactPayload()
    {
        using var fixture = new PayloadFixture();

        var files = PayloadInstaller.ValidateManifest(fixture.Root);

        Assert.Contains(AppIdentity.GetExecutableFileName(), files);
    }

    [Fact]
    public void ValidateManifest_AcceptsAddedPayloadFiles()
    {
        using var fixture = new PayloadFixture("future-sidecar.dll", "data/future-format.json");

        var files = PayloadInstaller.ValidateManifest(fixture.Root);

        Assert.Contains("future-sidecar.dll", files);
        Assert.Contains(
            Path.Combine("data", "future-format.json"),
            files,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateManifest_AcceptsPayloadWithoutPreviousSidecars()
    {
        using var fixture = new PayloadFixture();

        var files = PayloadInstaller.ValidateManifest(fixture.Root);

        Assert.Equal([AppIdentity.GetExecutableFileName()], files);
    }

    [Fact]
    public void ValidateManifest_RejectsUndeclaredFile()
    {
        using var fixture = new PayloadFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "unexpected.txt"), "unexpected");

        var exception = Assert.Throws<UserFacingException>(
            () => PayloadInstaller.ValidateManifest(fixture.Root));

        Assert.Contains("undeclared", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateManifest_RejectsPathTraversal()
    {
        using var fixture = new PayloadFixture();
        var manifest = new PayloadManifest { Files = ["../outside"] };
        File.WriteAllText(
            Path.Combine(fixture.Root, "payload-manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                KustoJsonSerializerContext.Default.PayloadManifest));

        var exception = Assert.Throws<UserFacingException>(
            () => PayloadInstaller.ValidateManifest(fixture.Root));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateManifest_RejectsMissingExecutable()
    {
        using var fixture = new PayloadFixture("future.dat");
        File.Delete(Path.Combine(fixture.Root, AppIdentity.GetExecutableFileName()));
        fixture.WriteManifest(["future.dat"]);

        var exception = Assert.Throws<UserFacingException>(
            () => PayloadInstaller.ValidateManifest(fixture.Root));

        Assert.Contains(AppIdentity.GetExecutableFileName(), exception.Message, StringComparison.Ordinal);
    }

    private sealed class PayloadFixture : IDisposable
    {
        public PayloadFixture(params string[] additionalFiles)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"kusto-payload-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            var files = new[] { AppIdentity.GetExecutableFileName() }
                .Concat(additionalFiles)
                .ToArray();
            foreach (var file in files)
            {
                var path = Path.Combine(Root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file);
            }

            WriteManifest(files);
        }

        public void WriteManifest(IEnumerable<string> files)
        {
            var manifest = new PayloadManifest
            {
                Files =
                [
                    .. files
                        .Select(path => path.Replace('\\', '/'))
                        .Order(StringComparer.Ordinal)
                ]
            };
            File.WriteAllText(
                Path.Combine(Root, "payload-manifest.json"),
                JsonSerializer.Serialize(
                    manifest,
                    KustoJsonSerializerContext.Default.PayloadManifest));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
