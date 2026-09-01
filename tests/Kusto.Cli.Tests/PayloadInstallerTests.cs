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

    private sealed class PayloadFixture : IDisposable
    {
        public PayloadFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"kusto-payload-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            var files = AppIdentity.GetExecutablePayloadFileNames().ToArray();
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(Root, file), file);
            }

            var manifest = new PayloadManifest { Files = [.. files] };
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
