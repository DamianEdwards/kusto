namespace Kusto.Cli.Tests;

public sealed class CompletionScriptsTests
{
    [Theory]
    [InlineData("bash", "complete -F _kusto_completion kusto")]
    [InlineData("zsh", "compdef _kusto kusto")]
    [InlineData("fish", "complete -f -c \"kusto\"")]
    [InlineData("pwsh", "Register-ArgumentCompleter -Native -CommandName 'kusto'")]
    public void TryGenerate_ProducesRegistrationForSupportedShell(
        string shell,
        string expected)
    {
        Assert.True(CompletionScripts.TryGenerate(
            shell,
            ["kusto"],
            out var script,
            out var error));
        Assert.Empty(error);
        Assert.Contains(expected, script, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGenerate_RejectsUnsupportedShell()
    {
        Assert.False(CompletionScripts.TryGenerate(
            "cmd",
            ["kusto"],
            out var script,
            out var error));
        Assert.Empty(script);
        Assert.Contains("Unsupported shell", error, StringComparison.Ordinal);
    }
}
