namespace Kusto.Cli;

public static class QueryTextResolver
{
    public static async Task<string> ResolveAsync(
        string? queryArgument,
        string? filePath,
        bool isInputRedirected,
        TextReader stdin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!string.IsNullOrWhiteSpace(queryArgument))
            {
                throw new UserFacingException("Provide either an inline query argument or --file, but not both.");
            }

            if (!File.Exists(filePath))
            {
                throw new UserFacingException($"Query file '{filePath}' was not found.");
            }

            return (await File.ReadAllTextAsync(filePath, cancellationToken)).Trim();
        }

        if (string.Equals(queryArgument, "-", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdinText = await stdin.ReadToEndAsync();
            return stdinText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(queryArgument))
        {
            return queryArgument;
        }

        if (isInputRedirected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdinText = await stdin.ReadToEndAsync();
            return stdinText.Trim();
        }

        throw new UserFacingException("No query text was provided. Supply an argument, --file, or '-' to read from stdin.");
    }
}
