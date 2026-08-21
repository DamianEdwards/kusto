using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public static class CliRunner
{
    private static readonly OutputFormat[] DefaultSupportedFormats =
    [
        OutputFormat.Human,
        OutputFormat.Json,
        OutputFormat.Markdown
    ];

    private static readonly OutputFormat[] RecognizedFormats =
    [
        OutputFormat.Human,
        OutputFormat.Json,
        OutputFormat.Markdown,
        OutputFormat.Csv
    ];

    public static Task<int> RunAsync(
        string formatToken,
        string? logLevelToken,
        Func<CliRuntime, CancellationToken, Task<CliOutput>> commandAction,
        CancellationToken cancellationToken)
        => RunAsync(formatToken, logLevelToken, commandAction, cancellationToken, DefaultSupportedFormats);

    public static async Task<int> RunAsync(
        string formatToken,
        string? logLevelToken,
        Func<CliRuntime, CancellationToken, Task<CliOutput>> commandAction,
        CancellationToken cancellationToken,
        params OutputFormat[] supportedFormats)
    {
        OutputFormat format;
        LogLevel? logLevel;
        supportedFormats = supportedFormats is { Length: > 0 }
            ? supportedFormats
            : DefaultSupportedFormats;

        try
        {
            format = ParseOutputFormatToken(formatToken);
            logLevel = ParseLogLevelToken(logLevelToken);
            EnsureSupportedOutputFormat(formatToken, format, supportedFormats);
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
            "csv" => OutputFormat.Csv,
            _ => throw new UserFacingException(
                $"'{formatToken}' is not a valid output format. Use one of: {DescribeOutputFormats(RecognizedFormats)}.")
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

        var configDirectory = FileConfigStore.ResolveConfigDirectory(configPath);
        var tokenCacheName = WamTokenCacheName.Resolve(configDirectory);
        var authDirectory = Path.Combine(configDirectory, "auth");

        var platform = SystemPlatform.Instance;
        var windowHandleProvider = new SystemWindowHandleProvider();
        var brokerCredentialFactory = new WamBrokerCredentialFactory();
        var authenticationRecordStore = new AuthenticationRecordStore(
            authDirectory,
            loggerFactory.CreateLogger<AuthenticationRecordStore>());

        // Metadata is read from the cluster's unauthenticated endpoint over a dedicated
        // client that never follows redirects, so a request can never be steered off-origin.
        var metadataHttpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var metadataProvider = new KustoAuthMetadataProvider(
            metadataHttpClient,
            loggerFactory.CreateLogger<KustoAuthMetadataProvider>());

        var defaultTokenProvider = new AzureTokenProvider();
        var wamTokenProvider = new WamTokenProvider(
            platform,
            metadataProvider,
            authenticationRecordStore,
            brokerCredentialFactory,
            windowHandleProvider,
            tokenCacheName,
            loggerFactory.CreateLogger<WamTokenProvider>());
        var tokenProvider = new RoutingTokenProvider(defaultTokenProvider, wamTokenProvider);

        var clusterAuthenticationService = new WamClusterAuthenticator(
            platform,
            metadataProvider,
            authenticationRecordStore,
            brokerCredentialFactory,
            windowHandleProvider,
            tokenCacheName,
            loggerFactory.CreateLogger<WamClusterAuthenticator>());

        var httpClient = new HttpClient();
        var kustoService = new KustoHttpService(httpClient, tokenProvider, loggerFactory.CreateLogger<KustoHttpService>());
        var settingsResolver = new SchemaCacheSettingsResolver();
        var offlineTableDataStore = new OfflineTableDataStore(settingsResolver, loggerFactory.CreateLogger<OfflineTableDataStore>());
        var tableSchemaProvider = new TableSchemaProvider(
            kustoService,
            offlineTableDataStore,
            settingsResolver,
            loggerFactory.CreateLogger<TableSchemaProvider>());
        var tableOfflineDataManager = new TableOfflineDataManager(
            kustoService,
            offlineTableDataStore,
            loggerFactory.CreateLogger<TableOfflineDataManager>());
        var confirmationPrompt = new ConsoleConfirmationPrompt(Console.In, stderrWriter ?? Console.Error);
        var formatter = new OutputFormatter();

        return new CliRuntime(
            loggerFactory,
            logger,
            httpClient,
            configStore,
            connectionResolver,
            kustoService,
            clusterAuthenticationService,
            tableSchemaProvider,
            tableOfflineDataManager,
            confirmationPrompt,
            formatter,
            metadataHttpClient);
    }

    private static void EnsureSupportedOutputFormat(string formatToken, OutputFormat format, IReadOnlyCollection<OutputFormat> supportedFormats)
    {
        if (supportedFormats.Contains(format))
        {
            return;
        }

        throw new UserFacingException(
            $"'{formatToken}' is not supported for this command. Use one of: {DescribeOutputFormats(supportedFormats)}.");
    }

    private static string DescribeOutputFormats(IEnumerable<OutputFormat> formats)
    {
        var uniqueFormats = new HashSet<OutputFormat>(formats);
        var tokens = new List<string>();

        if (uniqueFormats.Contains(OutputFormat.Human))
        {
            tokens.Add("human");
        }

        if (uniqueFormats.Contains(OutputFormat.Json))
        {
            tokens.Add("json");
        }

        if (uniqueFormats.Contains(OutputFormat.Markdown))
        {
            tokens.Add("markdown");
            tokens.Add("md");
        }

        if (uniqueFormats.Contains(OutputFormat.Csv))
        {
            tokens.Add("csv");
        }

        return string.Join(", ", tokens);
    }
}
