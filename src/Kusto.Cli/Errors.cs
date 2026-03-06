using Azure.Identity;

namespace Kusto.Cli;

public sealed class UserFacingException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}

public static class ErrorMapper
{
    public static string Map(Exception exception)
    {
        return exception switch
        {
            UserFacingException userFacingException => userFacingException.Message,
            CredentialUnavailableException => "No Azure credential could be resolved. Run 'az login' and verify access to the target cluster. For sovereign clouds, make sure Azure CLI is set to the matching cloud with 'az cloud set'.",
            AuthenticationFailedException => "Authentication failed. Run 'az login' and verify that you can access this cluster. For sovereign clouds, make sure Azure CLI is set to the matching cloud with 'az cloud set'.",
            HttpRequestException => "The request to Kusto failed. Verify the cluster URL and your network connectivity.",
            TaskCanceledException => "The request timed out before Kusto responded.",
            _ => "The command failed unexpectedly. Check the log file for details."
        };
    }
}
