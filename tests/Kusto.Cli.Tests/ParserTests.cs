using System.CommandLine;

namespace Kusto.Cli.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parse_AllowsMarkdownAlias()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["cluster", "list", "--format", "md"], new ParserConfiguration());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_RejectsUnknownFormat()
    {
        var rootCommand = CommandFactory.CreateRootCommand();
        var result = rootCommand.Parse(["cluster", "list", "--format", "csv"], new ParserConfiguration());
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("trace")]
    [InlineData("Warning")]
    [InlineData("Critical")]
    public void ParseLogLevelToken_AcceptsValidValues(string value)
    {
        var parsed = CliRunner.ParseLogLevelToken(value);
        Assert.NotNull(parsed);
    }
}
