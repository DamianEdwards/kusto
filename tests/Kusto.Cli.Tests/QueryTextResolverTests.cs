namespace Kusto.Cli.Tests;

public sealed class QueryTextResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsInlineQuery()
    {
        var query = await QueryTextResolver.ResolveAsync("StormEvents | take 1", null, false, TextReader.Null, CancellationToken.None);
        Assert.Equal("StormEvents | take 1", query);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsFileQuery()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "StormEvents | count");

        var query = await QueryTextResolver.ResolveAsync(null, path, false, TextReader.Null, CancellationToken.None);
        Assert.Equal("StormEvents | count", query);

        File.Delete(path);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsStdinWhenDashSpecified()
    {
        using var stdin = new StringReader("StormEvents | limit 10");
        var query = await QueryTextResolver.ResolveAsync("-", null, false, stdin, CancellationToken.None);
        Assert.Equal("StormEvents | limit 10", query);
    }
}
