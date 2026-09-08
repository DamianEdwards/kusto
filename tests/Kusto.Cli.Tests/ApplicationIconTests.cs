using System.Xml.Linq;

namespace Kusto.Cli.Tests;

public sealed class ApplicationIconTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void ProjectDefinesMultiResolutionApplicationIcon()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "Kusto.Cli", "Kusto.Cli.csproj");
        var project = XDocument.Load(projectPath);
        var applicationIcon = Assert.Single(project.Descendants("ApplicationIcon"));

        Assert.Equal("Assets/kusto.ico", applicationIcon.Value);

        var iconPath = Path.GetFullPath(applicationIcon.Value, Path.GetDirectoryName(projectPath)!);
        Assert.True(File.Exists(iconPath), $"Application icon not found at '{iconPath}'.");

        using var stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var imageCount = reader.ReadUInt16();
        Assert.Equal(ExpectedSizes.Length, imageCount);

        var entries = Enumerable.Range(0, imageCount)
            .Select(_ => ReadEntry(reader))
            .ToArray();

        Assert.Equal(ExpectedSizes, entries.Select(entry => entry.Width));
        Assert.All(entries, entry =>
        {
            Assert.Equal(entry.Width, entry.Height);
            Assert.Equal(1, entry.Planes);
            Assert.Equal(32, entry.BitsPerPixel);
            Assert.InRange(entry.DataOffset + entry.DataLength, 0u, (uint)stream.Length);

            stream.Position = entry.DataOffset;
            Assert.Equal(PngSignature, reader.ReadBytes(PngSignature.Length));
        });
    }

    private static IconEntry ReadEntry(BinaryReader reader)
    {
        var width = ReadDimension(reader.ReadByte());
        var height = ReadDimension(reader.ReadByte());
        reader.ReadByte();
        reader.ReadByte();

        return new IconEntry(
            width,
            height,
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
    }

    private static int ReadDimension(byte value) => value == 0 ? 256 : value;

    private static string FindRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "kusto.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Could not locate the repository root above the test output directory.");
    }

    private readonly record struct IconEntry(
        int Width,
        int Height,
        ushort Planes,
        ushort BitsPerPixel,
        uint DataLength,
        uint DataOffset);
}
