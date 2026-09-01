using System.CommandLine;
using System.CommandLine.Completions;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

internal static class MaintenanceCommands
{
    public static Command CreateCompletionsCommand()
    {
        var command = new Command("completions", "Generate shell completion scripts.");
        command.Aliases.Add("completion");

        var scriptCommand = new Command("script", "Generate a shell completion script.");
        var shellArgument = new Argument<string?>("shell")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Shell to generate a completion script for."
        };
        shellArgument.CompletionSources.Add(
            _ => CompletionScripts.SupportedShells.Select(shell => new CompletionItem(shell)));
        var commandNameOption = new Option<string[]>("--command-name")
        {
            Description =
                "Command name to register. May be specified more than once."
        };
        commandNameOption.AllowMultipleArgumentsPerToken = false;
        scriptCommand.Arguments.Add(shellArgument);
        scriptCommand.Options.Add(commandNameOption);
        scriptCommand.SetAction(parseResult =>
        {
            var shell = parseResult.GetValue(shellArgument)
                ?? CompletionScripts.GetDefaultShell();
            var names = parseResult.GetValue(commandNameOption);
            var effectiveNames = names is { Length: > 0 }
                ? names
                : [AppIdentity.CommandName];
            if (!CompletionScripts.TryGenerate(
                    shell,
                    effectiveNames,
                    out var script,
                    out var error))
            {
                Console.Error.WriteLine($"kusto: {error}");
                return 1;
            }

            Console.Out.Write(script);
            return 0;
        });

        command.Subcommands.Add(scriptCommand);
        return command;
    }

    public static Command CreateConfigCommand(
        Option<string> formatOption,
        Option<string?> logLevelOption)
    {
        var command = new Command(
            "config",
            "Show or update global Kusto CLI configuration.");
        var setOption = new Option<string?>("--set")
        {
            Description =
                "Set a value as key=value. Supported key: include_prerelease_updates."
        };
        command.Options.Add(setOption);
        command.SetAction((parseResult, cancellationToken) =>
            CliRunner.RunAsync(
                parseResult.GetRequiredValue(formatOption),
                parseResult.GetValue(logLevelOption),
                async (runtime, ct) =>
                {
                    var config = await runtime.ConfigStore.LoadAsync(ct);
                    var assignment = parseResult.GetValue(setOption);
                    if (!string.IsNullOrWhiteSpace(assignment))
                    {
                        var separator = assignment.IndexOf('=');
                        if (separator <= 0)
                        {
                            throw new UserFacingException(
                                "Use --set key=value.");
                        }

                        var key = assignment[..separator].Trim();
                        var value = assignment[(separator + 1)..].Trim();
                        if (!key.Equals(
                                "include_prerelease_updates",
                                StringComparison.OrdinalIgnoreCase)
                            || !TryParseBoolean(value, out var enabled))
                        {
                            throw new UserFacingException(
                                "include_prerelease_updates must be true or false.");
                        }

                        config.IncludePrereleaseUpdates = enabled;
                        await runtime.ConfigStore.SaveAsync(config, ct);
                    }

                    return new CliOutput
                    {
                        Properties = new Dictionary<string, string?>
                        {
                            ["Config path"] = FileConfigStore.ResolveConfigPath(),
                            ["Include prerelease updates"] =
                                config.IncludePrereleaseUpdates.ToString().ToLowerInvariant()
                        }
                    };
                },
                cancellationToken));
        return command;
    }

    public static Command CreateUpdateCommand(Option<string?> logLevelOption)
    {
        var command = new Command("update", "Check for and install Kusto CLI updates.");
        var checkOption = new Option<bool>("--check")
        {
            Description = "Check without downloading or installing."
        };
        var preReleaseOption = new Option<bool>("--pre-release")
        {
            Description = "Include official prerelease versions."
        };
        preReleaseOption.Aliases.Add("-p");
        var stableOnlyOption = new Option<bool>("--stable-only")
        {
            Description =
                "Only consider stable releases, overriding the configured prerelease preference."
        };
        var skipProvenanceOption = new Option<bool>("--skip-provenance-checks")
        {
            Description =
                "Skip Authenticode or GitHub attestation verification. Checksums are still required."
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show the selected update without changing files."
        };

        command.Options.Add(checkOption);
        command.Options.Add(preReleaseOption);
        command.Options.Add(stableOnlyOption);
        command.Options.Add(skipProvenanceOption);
        command.Options.Add(dryRunOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var requestedLogLevel = CliRunner.ParseLogLevelToken(
                parseResult.GetValue(logLevelOption));
            using var loggerFactory = LoggingFactoryBuilder.Create(requestedLogLevel);
            var logger = loggerFactory.CreateLogger("Update");
            try
            {
                var usePreRelease = parseResult.GetValue(preReleaseOption);
                var stableOnly = parseResult.GetValue(stableOnlyOption);
                if (usePreRelease && stableOnly)
                {
                    throw new UserFacingException(
                        "--pre-release and --stable-only cannot be used together.");
                }

                var config = await new FileConfigStore().LoadAsync(cancellationToken);
                var allowPreRelease =
                    !stableOnly && (usePreRelease || config.IncludePrereleaseUpdates);
                var stateStore = new UpdateStateStore(
                    loggerFactory.CreateLogger<UpdateStateStore>());
                var releaseService = new GitHubReleaseService(
                    loggerFactory.CreateLogger<GitHubReleaseService>());
                var provenanceVerifier = new ProvenanceVerifier(
                    loggerFactory.CreateLogger<ProvenanceVerifier>());
                var service = new UpdateService(
                    releaseService,
                    provenanceVerifier,
                    stateStore,
                    loggerFactory.CreateLogger<UpdateService>());

                Console.Out.WriteLine("Checking for updates...");
                var update = await service.CheckForUpdateAsync(
                    allowPreRelease,
                    stableOnly,
                    cancellationToken);
                if (update is null)
                {
                    Console.Out.WriteLine("kusto is up to date.");
                    return 0;
                }

                Console.Out.WriteLine(
                    $"Update available: {update.CurrentVersion} -> {update.AvailableVersion}");
                if (parseResult.GetValue(checkOption))
                {
                    Console.Out.WriteLine("Run 'kusto update' to install it.");
                    return 0;
                }

                if (parseResult.GetValue(dryRunOption))
                {
                    Console.Out.WriteLine(
                        $"[dry-run] Would download, verify, and install release {update.ReleaseTag}.");
                    Console.Out.WriteLine(
                        $"[dry-run] Checksum verification: enabled; provenance: {(update.IsDevBuild || parseResult.GetValue(skipProvenanceOption) ? "skipped" : "enabled")}.");
                    return 0;
                }

                Console.Out.WriteLine("Downloading and verifying update...");
                await service.StageAsync(
                    update,
                    parseResult.GetValue(skipProvenanceOption),
                    cancellationToken);
                var result = await service.InstallStagedAsync(cancellationToken);
                if (result.Scheduled)
                {
                    Console.Out.WriteLine(
                        "The verified update is staged and will finish installing after this process exits.");
                }
                else
                {
                    Console.Out.WriteLine(
                        $"kusto updated to {update.AvailableVersion}.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Update command failed");
                Console.Error.WriteLine($"kusto: {ErrorMapper.Map(ex)}");
                return 1;
            }
        });

        return command;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
