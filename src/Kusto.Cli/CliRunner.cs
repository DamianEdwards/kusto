using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public static class CliRunner
{
    public static async Task<int> RunAsync(
        string formatToken,
        string? logLevelToken,
        Func<CliRuntime, CancellationToken, Task<CliOutput>> commandAction,
        CancellationToken cancellationToken)
    {
        OutputFormat format;
        LogLevel? logLevel;

        try
        {
            format = ParseOutputFormatToken(formatToken);
            logLevel = ParseLogLevelToken(logLevelToken);
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError(ErrorMapper.Map(ex));
            return 1;
        }

        using var runtime = CreateRuntime(logLevel);

        try
        {
            var output = await commandAction(runtime, cancellationToken);
            var renderedOutput = runtime.OutputFormatter.Format(output, format);
            if (!string.IsNullOrWhiteSpace(renderedOutput))
            {
                Console.Out.WriteLine(renderedOutput);
            }

            return 0;
        }
        catch (Exception ex)
        {
            runtime.Logger.LogError(ex, "Command execution failed.");
            ConsoleOutput.WriteError(ErrorMapper.Map(ex));
            return 1;
        }
    }

    public static OutputFormat ParseOutputFormatToken(string formatToken)
    {
        return formatToken.ToLowerInvariant() switch
        {
            "human" => OutputFormat.Human,
            "json" => OutputFormat.Json,
            "markdown" => OutputFormat.Markdown,
            "md" => OutputFormat.Markdown,
            _ => throw new UserFacingException($"'{formatToken}' is not a valid output format. Use one of: human, json, markdown, md.")
        };
    }

    public static LogLevel? ParseLogLevelToken(string? logLevelToken)
    {
        if (string.IsNullOrWhiteSpace(logLevelToken))
        {
            return null;
        }

        if (Enum.TryParse<LogLevel>(logLevelToken, true, out var parsed))
        {
            return parsed;
        }

        throw new UserFacingException(
            $"'{logLevelToken}' is not a valid log level. Use Trace, Debug, Information, Warning, Error, Critical, or None.");
    }

    public static CliRuntime CreateRuntime(LogLevel? requestedLogLevel, string? configPath = null, TextWriter? stderrWriter = null, string? logFilePath = null)
    {
        var loggerFactory = LoggingFactoryBuilder.Create(requestedLogLevel, logFilePath, stderrWriter);
        var logger = loggerFactory.CreateLogger("kusto");
        var configStore = new FileConfigStore(configPath);
        var connectionResolver = new KustoConnectionResolver();
        var tokenProvider = new AzureTokenProvider();
        var httpClient = new HttpClient();
        var kustoService = new KustoHttpService(httpClient, tokenProvider, loggerFactory.CreateLogger<KustoHttpService>());
        var tableSchemaProvider = new TableSchemaProvider(
            kustoService,
            new SchemaCacheSettingsResolver(),
            loggerFactory.CreateLogger<TableSchemaProvider>());
        var formatter = new OutputFormatter();

        return new CliRuntime(
            loggerFactory,
            logger,
            httpClient,
            configStore,
            connectionResolver,
            kustoService,
            tableSchemaProvider,
            formatter);
    }
}
