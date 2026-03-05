using Azure.Identity;

namespace Kusto.Cli.Tests;

public sealed class ErrorMapperTests
{
    [Fact]
    public void Map_DoesNotExposeRawUnexpectedExceptionMessage()
    {
        var message = ErrorMapper.Map(new InvalidOperationException("sensitive implementation detail"));
        Assert.DoesNotContain("sensitive implementation detail", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_ProvidesFriendlyAuthMessage()
    {
        var message = ErrorMapper.Map(new AuthenticationFailedException("auth failed"));
        Assert.Contains("Authentication failed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_CredentialUnavailablePromptsAzLogin()
    {
        var message = ErrorMapper.Map(new CredentialUnavailableException("credential unavailable"));
        Assert.Contains("az login", message, StringComparison.OrdinalIgnoreCase);
    }
}
