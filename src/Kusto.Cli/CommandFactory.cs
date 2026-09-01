using System.CommandLine;

namespace Kusto.Cli;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand()
    {
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format for people or tools: human, json, markdown, md, csv (query only).",
            Recursive = true,
            DefaultValueFactory = _ => "human"
        };
        formatOption.AcceptOnlyFromAmong("human", "json", "markdown", "md", "csv");

        var logLevelOption = new Option<string?>("--log-level")
        {
            Description = "Console log level (Trace, Debug, Information, Warning, Error, Critical, None).",
            Recursive = true
        };

        var root = new RootCommand("Query Azure Data Explorer (Kusto) from the terminal: save clusters, pick defaults, inspect databases and tables, and run KQL.")
        {
            formatOption,
            logLevelOption,
            MaintenanceCommands.CreateCompletionsCommand(),
            MaintenanceCommands.CreateConfigCommand(formatOption, logLevelOption),
            MaintenanceCommands.CreateUpdateCommand(logLevelOption),
            BuildExamplesCommand(formatOption, logLevelOption),
            BuildClusterCommand(formatOption, logLevelOption),
            BuildDatabaseCommand(formatOption, logLevelOption),
            BuildTableCommand(formatOption, logLevelOption),
            BuildQueryCommand(formatOption, logLevelOption),
            BuildDiagnosticsCommand()
        };
        return root;
    }

    private static Command BuildDiagnosticsCommand()
    {
        // Hidden top-level command for packaging/runtime smoke tests. The first
        // subcommand exists so CI can verify that the released archive can load
        // SkiaSharp/HarfBuzz native sidecars and produce a real PNG without
        // requiring authenticated access to a Kusto cluster.
        var diagCommand = new Command("_diag", "Diagnostic commands for packaging/runtime smoke tests.")
        {
            Hidden = true
        };

        var chartSelfTest = new Command("chart-self-test", "Render a fixed sample chart to a PNG path. Used to verify native chart-rendering dependencies on a fresh install.")
        {
            Hidden = true
        };

        var outputOption = new Option<string>("--output")
        {
            Description = "Path to the PNG file to write.",
            Required = true
        };

        chartSelfTest.Add(outputOption);
        chartSelfTest.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.GetRequiredValue(outputOption);

            var chart = new QueryChartDefinition
            {
                Kind = QueryChartKind.Column,
                Title = "kusto chart self-test",
                XTitle = "category",
                YTitle = "value",
                Categories = ["alpha", "beta", "gamma", "delta"],
                Series = [new QueryChartSeries("sample", [10, 25, 15, 40])]
            };

            try
            {
                var written = await ChartImageWriter.WritePngAsync(
                    chart,
                    output,
                    ChartStyle.DefaultWidth,
                    ChartStyle.DefaultHeight,
                    cancellationToken);

                Console.Out.WriteLine($"Chart self-test written to {written}");
                return 0;
            }
            catch (UserFacingException ex)
            {
                Console.Error.WriteLine($"kusto: {ex.Message}");
                return 1;
            }
        });

        diagCommand.Add(chartSelfTest);
        return diagCommand;
    }

    private static Command BuildExamplesCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var examplesCommand = new Command("examples", "Show usage examples, aliases, and quick-start commands.");
        examplesCommand.Aliases.Add("example");
        examplesCommand.Aliases.Add("aliases");
        examplesCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            return CliRunner.RunAsync(format, logLevel, static (_, _) =>
                Task.FromResult(new CliOutput
                {
                                        Table = new TabularData(
                        ["Section", "Example"],
                        [
                            ["Quick start", "kusto cluster add help https://help.kusto.windows.net/ --use"],
                            ["Quick start", "kusto database set-default Samples --cluster help"],
                            ["Browse", "kusto table list --cluster help --database Samples --filter \"^Storm\" --take 10"],
                            ["Browse", "kusto table show StormEvents --cluster help --database Samples"],
                            ["Run KQL", "kusto query \"StormEvents | take 5\" --cluster help --database Samples"],
                            ["Run KQL", "kusto query --chart \"StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart\" --cluster help --database Samples"],
                            ["Run KQL", "kusto query --format markdown --chart \"StormEvents | summarize Count=count() by State | top 5 by Count desc | render piechart\" --cluster help --database Samples"],
                            ["Run KQL", "kusto query \"StormEvents | summarize EventCount=count() by State | top 10 by EventCount desc\" --format csv --cluster help --database Samples > top-states.csv"],
                            ["Run KQL", "kusto query --file .\\queries\\top-states.kql --cluster help --database Samples"],
                            ["Optional aliases", "aliases | clusters | db | databases | tables | ls | get | schema | rm | delete | use | run | exec | --db | --limit | -f"]
                        ])
                }), cancellationToken);
        });

        return examplesCommand;
    }

    private static Command BuildClusterCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var clusterCommand = new Command("cluster", "Manage saved clusters and the active cluster.");
        clusterCommand.Aliases.Add("clusters");

        var listCommand = new Command("list", "List saved clusters and show which one is active.");
        listCommand.Aliases.Add("ls");
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                if (config.Clusters.Count == 0)
                {
                    return new CliOutput
                    {
                        Message = "No known clusters. Add one with: kusto cluster add <name> <url>"
                    };
                }

                var rows = new List<IReadOnlyList<string?>>();
                foreach (var cluster in config.Clusters.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    config.DefaultDatabases.TryGetValue(cluster.Url, out var defaultDatabase);
                    rows.Add(
                    [
                        cluster.Name,
                        cluster.Url,
                        string.Equals(config.DefaultClusterUrl, cluster.Url, StringComparison.OrdinalIgnoreCase) ? "*" : string.Empty,
                        defaultDatabase
                    ]);
                }

                return new CliOutput
                {
                    Table = new TabularData(["Name", "Url", "Default", "DefaultDatabase"], rows)
                };
            }, cancellationToken);
        });

        var clusterReferenceArgument = new Argument<string>("cluster")
        {
            Description = "Saved cluster name or cluster URL."
        };

        var showCommand = new Command("show", "Show a saved cluster, including its URL and default database.")
        {
            clusterReferenceArgument
        };
        showCommand.Aliases.Add("get");
        showCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            var clusterReference = parseResult.GetRequiredValue(clusterReferenceArgument);
            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var cluster = ClusterUtilities.FindKnownCluster(config, clusterReference) ??
                    throw new UserFacingException($"Cluster '{clusterReference}' is not known.");

                var normalizedUrl = ClusterUtilities.NormalizeClusterUrl(cluster.Url);
                config.DefaultDatabases.TryGetValue(normalizedUrl, out var defaultDatabase);
                return new CliOutput
                {
                    Properties = new Dictionary<string, string?>
                    {
                        ["Name"] = cluster.Name,
                        ["Url"] = normalizedUrl,
                        ["Default"] = string.Equals(config.DefaultClusterUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) ? "true" : "false",
                        ["DefaultDatabase"] = defaultDatabase
                    }
                };
            }, cancellationToken);
        });

        var addCommand = new Command("add", "Save a cluster name and URL for reuse. Use --use to also make it the default.");
        var clusterNameArgument = new Argument<string>("name") { Description = "Friendly cluster name." };
        var clusterUrlArgument = new Argument<string>("url") { Description = "Azure Data Explorer cluster URL." };
        var useOption = new Option<bool>("--use") { Description = "Also set this cluster as the active/default cluster." };
        addCommand.Add(clusterNameArgument);
        addCommand.Add(clusterUrlArgument);
        addCommand.Add(useOption);
        addCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            var name = parseResult.GetRequiredValue(clusterNameArgument);
            var url = parseResult.GetRequiredValue(clusterUrlArgument);
            var setAsDefault = parseResult.GetValue(useOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var normalizedUrl = ClusterUtilities.NormalizeClusterUrl(url);
                if (ClusterUtilities.FindKnownCluster(config, name) is not null)
                {
                    throw new UserFacingException($"A cluster with the name '{name}' already exists.");
                }

                if (ClusterUtilities.FindKnownCluster(config, normalizedUrl) is not null)
                {
                    throw new UserFacingException($"A cluster with URL '{normalizedUrl}' already exists.");
                }

                config.Clusters.Add(new KnownCluster
                {
                    Name = name,
                    Url = normalizedUrl
                });

                if (setAsDefault || string.IsNullOrWhiteSpace(config.DefaultClusterUrl))
                {
                    config.DefaultClusterUrl = normalizedUrl;
                }

                await runtime.ConfigStore.SaveAsync(config, ct);
                var message = setAsDefault
                    ? $"Added cluster '{name}' ({normalizedUrl}) and set it as default."
                    : $"Added cluster '{name}' ({normalizedUrl}).";
                return new CliOutput { Message = message };
            }, cancellationToken);
        });

        var removeCommand = new Command("remove", "Remove a saved cluster and its default database mapping.")
        {
            clusterReferenceArgument
        };
        removeCommand.Aliases.Add("rm");
        removeCommand.Aliases.Add("delete");
        removeCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            var clusterReference = parseResult.GetRequiredValue(clusterReferenceArgument);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var cluster = ClusterUtilities.FindKnownCluster(config, clusterReference) ??
                    throw new UserFacingException($"Cluster '{clusterReference}' is not known.");

                var normalizedUrl = ClusterUtilities.NormalizeClusterUrl(cluster.Url);
                config.Clusters.Remove(cluster);
                config.DefaultDatabases.Remove(normalizedUrl);
                if (string.Equals(config.DefaultClusterUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    config.DefaultClusterUrl = null;
                }

                await runtime.ConfigStore.SaveAsync(config, ct);
                return new CliOutput { Message = $"Removed cluster '{cluster.Name}'." };
            }, cancellationToken);
        });

        var setDefaultCommand = new Command("set-default", "Set the active/default cluster used when --cluster is omitted.")
        {
            clusterReferenceArgument
        };
        setDefaultCommand.Aliases.Add("use");
        setDefaultCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            var clusterReference = parseResult.GetRequiredValue(clusterReferenceArgument);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var cluster = ClusterUtilities.FindKnownCluster(config, clusterReference) ??
                    throw new UserFacingException($"Cluster '{clusterReference}' is not known.");

                config.DefaultClusterUrl = ClusterUtilities.NormalizeClusterUrl(cluster.Url);
                await runtime.ConfigStore.SaveAsync(config, ct);
                return new CliOutput { Message = $"Default cluster set to '{cluster.Name}'." };
            }, cancellationToken);
        });

        clusterCommand.Add(listCommand);
        clusterCommand.Add(showCommand);
        clusterCommand.Add(addCommand);
        clusterCommand.Add(removeCommand);
        clusterCommand.Add(setDefaultCommand);
        return clusterCommand;
    }

    private static Command BuildDatabaseCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var clusterOption = CreateClusterOption();
        var filterOption = CreateFilterOption("database");
        var takeOption = CreateTakeOption("databases");

        var databaseCommand = new Command("database", "Inspect databases and manage the active database.");
        databaseCommand.Aliases.Add("databases");
        databaseCommand.Aliases.Add("db");

        var listCommand = new Command("list", "List databases in a cluster. Use --filter or --limit to narrow results.")
        {
            clusterOption,
            filterOption,
            takeOption
        };
        listCommand.Aliases.Add("ls");
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var clusterReference = parseResult.GetValue(clusterOption);
            var filterValue = parseResult.GetValue(filterOption);
            var takeValue = parseResult.GetValue(takeOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var query = ListQueryBuilder.Build(".show databases | project DatabaseName", "DatabaseName", filterValue, takeValue);

                var databases = await runtime.KustoService.ExecuteManagementCommandAsync(
                    resolvedCluster.Url,
                    null,
                    query.Command,
                    query.Parameters,
                    ct);

                var rows = new List<IReadOnlyList<string?>>();
                var nameColumnIndex = GetPreferredColumnIndex(databases, "DatabaseName");
                config.DefaultDatabases.TryGetValue(resolvedCluster.Url, out var defaultDatabase);
                foreach (var row in databases.Rows)
                {
                    var databaseName = nameColumnIndex >= 0 && row.Count > nameColumnIndex ? row[nameColumnIndex] : string.Empty;
                    rows.Add([databaseName, string.Equals(databaseName, defaultDatabase, StringComparison.OrdinalIgnoreCase) ? "*" : string.Empty]);
                }

                return new CliOutput
                {
                    Table = new TabularData(["Database", "Default"], rows)
                };
            }, cancellationToken);
        });

        var databaseArgument = new Argument<string>("database")
        {
            Description = "Database name."
        };

        var showCommand = new Command("show", "Show details for a database.")
        {
            databaseArgument,
            clusterOption
        };
        showCommand.Aliases.Add("get");
        showCommand.SetAction((parseResult, cancellationToken) =>
        {
            var databaseName = parseResult.GetRequiredValue(databaseArgument);
            var clusterReference = parseResult.GetValue(clusterOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var command = $".show databases details | where DatabaseName =~ '{EscapeKustoLiteral(databaseName)}'";
                var result = await runtime.KustoService.ExecuteManagementCommandAsync(resolvedCluster.Url, null, command, null, ct);

                if (result.Rows.Count == 0)
                {
                    throw new UserFacingException($"Database '{databaseName}' was not found.");
                }

                return new CliOutput
                {
                    Properties = ConvertRowToProperties(result, 0)
                };
            }, cancellationToken);
        });

        var setDefaultCommand = new Command("set-default", "Set the default database used for a cluster when --database is omitted.")
        {
            databaseArgument,
            clusterOption
        };
        setDefaultCommand.Aliases.Add("use");
        setDefaultCommand.SetAction((parseResult, cancellationToken) =>
        {
            var databaseName = parseResult.GetRequiredValue(databaseArgument);
            var clusterReference = parseResult.GetValue(clusterOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var verifyCommand = $".show databases details | where DatabaseName =~ '{EscapeKustoLiteral(databaseName)}'";
                var result = await runtime.KustoService.ExecuteManagementCommandAsync(resolvedCluster.Url, null, verifyCommand, null, ct);
                if (result.Rows.Count == 0)
                {
                    throw new UserFacingException($"Database '{databaseName}' was not found.");
                }

                config.DefaultDatabases[resolvedCluster.Url] = databaseName;
                await runtime.ConfigStore.SaveAsync(config, ct);

                return new CliOutput
                {
                    Message = $"Default database for '{resolvedCluster.Url}' set to '{databaseName}'."
                };
            }, cancellationToken);
        });

        databaseCommand.Add(listCommand);
        databaseCommand.Add(showCommand);
        databaseCommand.Add(setDefaultCommand);
        return databaseCommand;
    }

    private static Command BuildTableCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var rootTableArgument = new Argument<string?>("table")
        {
            Description = "Table name.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var rootClusterOption = CreateClusterOption();
        var rootDatabaseOption = CreateDatabaseOption();
        var exportOfflineDataOption = CreateOfflineDataFileOption("--export-offline-data", "Export all offline table data to a JSON file.");
        var importOfflineDataOption = CreateOfflineDataFileOption("--import-offline-data", "Import offline table data from a JSON file.");
        var purgeOfflineDataOption = new Option<bool>("--purge-offline-data")
        {
            Description = "Purge offline data for tables that no longer exist. Alias: -p."
        };
        purgeOfflineDataOption.Aliases.Add("-p");
        var clearOfflineDataOption = new Option<bool>("--clear-offline-data")
        {
            Description = "Clear offline data for one table or all tables. Alias: -c."
        };
        clearOfflineDataOption.Aliases.Add("-c");
        var rootForceOption = CreateForceOption("Skip the confirmation prompt for destructive offline-data operations.");

        var listClusterOption = CreateClusterOption();
        var listDatabaseOption = CreateDatabaseOption();
        var filterOption = CreateFilterOption("table");
        var takeOption = CreateTakeOption("tables");

        var showTableArgument = new Argument<string>("table")
        {
            Description = "Table name."
        };
        var showClusterOption = CreateClusterOption();
        var showDatabaseOption = CreateDatabaseOption();
        var refreshOfflineDataOption = new Option<bool>("--refresh-offline-data")
        {
            Description = "Fetch the latest table schema and update local offline data. Alias: -r."
        };
        refreshOfflineDataOption.Aliases.Add("-r");

        var notesTableArgument = new Argument<string?>("table")
        {
            Description = "Table name.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var notesClusterOption = CreateClusterOption();
        var notesDatabaseOption = CreateDatabaseOption();
        var addNoteOption = new Option<string?>("--add")
        {
            Description = "Add a note for the specified table. Alias: -a."
        };
        addNoteOption.Aliases.Add("-a");
        var noteIdOption = new Option<int?>("--id")
        {
            Description = "Show a specific note by its sequential ID."
        };
        var deleteNoteOption = new Option<int?>("--delete")
        {
            Description = "Delete a note by its sequential ID. Alias: -d."
        };
        deleteNoteOption.Aliases.Add("-d");
        var clearNotesOption = new Option<bool>("--clear")
        {
            Description = "Clear notes for one table or all tables. Alias: -c."
        };
        clearNotesOption.Aliases.Add("-c");
        var notesForceOption = CreateForceOption("Skip the confirmation prompt when clearing notes.");

        var tableCommand = new Command("table", "Browse tables and inspect schema.");
        tableCommand.Aliases.Add("tables");
        tableCommand.Add(rootTableArgument);
        tableCommand.Add(rootClusterOption);
        tableCommand.Add(rootDatabaseOption);
        tableCommand.Add(exportOfflineDataOption);
        tableCommand.Add(importOfflineDataOption);
        tableCommand.Add(purgeOfflineDataOption);
        tableCommand.Add(clearOfflineDataOption);
        tableCommand.Add(rootForceOption);
        tableCommand.SetAction((parseResult, cancellationToken) =>
        {
            var tableName = parseResult.GetValue(rootTableArgument);
            var clusterReference = parseResult.GetValue(rootClusterOption);
            var databaseName = parseResult.GetValue(rootDatabaseOption);
            var exportFile = parseResult.GetValue(exportOfflineDataOption);
            var importFile = parseResult.GetValue(importOfflineDataOption);
            var purgeOfflineData = parseResult.GetValue(purgeOfflineDataOption);
            var clearOfflineData = parseResult.GetValue(clearOfflineDataOption);
            var force = parseResult.GetValue(rootForceOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var selectedActionCount = CountSpecified(
                    exportFile is not null,
                    importFile is not null,
                    purgeOfflineData,
                    clearOfflineData);

                if (selectedActionCount == 0)
                {
                    if (!string.IsNullOrWhiteSpace(tableName))
                    {
                        throw new UserFacingException($"Use 'kusto table show {tableName}' to inspect a table or add --clear-offline-data to remove cached data.");
                    }

                    throw new UserFacingException("Specify a table subcommand or an offline-data option.");
                }

                if (selectedActionCount > 1)
                {
                    throw new UserFacingException("Specify only one offline-data action at a time.");
                }

                if (force && !purgeOfflineData && !clearOfflineData)
                {
                    throw new UserFacingException("--force can only be used with --purge-offline-data or --clear-offline-data.");
                }

                var config = await runtime.ConfigStore.LoadAsync(ct);

                if (exportFile is not null)
                {
                    EnsureNoScopedTableOptions(tableName, clusterReference, databaseName, "--export-offline-data");
                    return await runtime.TableOfflineDataManager.ExportOfflineDataAsync(config, exportFile.FullName, ct);
                }

                if (importFile is not null)
                {
                    EnsureNoScopedTableOptions(tableName, clusterReference, databaseName, "--import-offline-data");
                    return await runtime.TableOfflineDataManager.ImportOfflineDataAsync(config, importFile.FullName, ct);
                }

                if (purgeOfflineData)
                {
                    EnsureNoScopedTableOptions(tableName, clusterReference, databaseName, "--purge-offline-data");
                    if (!force && !await runtime.ConfirmationPrompt.ConfirmAsync("Purge offline data for tables that no longer exist?", ct))
                    {
                        return new CliOutput { Message = "Operation cancelled." };
                    }

                    return await runtime.TableOfflineDataManager.PurgeOfflineDataAsync(config, ct);
                }

                if (!clearOfflineData)
                {
                    throw new UserFacingException("Specify an offline-data action to run.");
                }

                if (string.IsNullOrWhiteSpace(tableName))
                {
                    if (!string.IsNullOrWhiteSpace(clusterReference) || !string.IsNullOrWhiteSpace(databaseName))
                    {
                        throw new UserFacingException("--cluster and --database can only be used with --clear-offline-data when a table name is provided.");
                    }

                    if (!force && !await runtime.ConfirmationPrompt.ConfirmAsync("Clear all offline table data?", ct))
                    {
                        return new CliOutput { Message = "Operation cancelled." };
                    }

                    return await runtime.TableOfflineDataManager.ClearOfflineDataAsync(config, null, null, null, ct);
                }

                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);
                if (!force && !await runtime.ConfirmationPrompt.ConfirmAsync($"Clear offline data for table '{tableName}'?", ct))
                {
                    return new CliOutput { Message = "Operation cancelled." };
                }

                return await runtime.TableOfflineDataManager.ClearOfflineDataAsync(
                    config,
                    resolvedCluster.Url,
                    resolvedDatabase,
                    tableName,
                    ct);
            }, cancellationToken);
        });

        var listCommand = new Command("list", "List tables in a database. Use --filter or --limit to narrow results.")
        {
            listClusterOption,
            listDatabaseOption,
            filterOption,
            takeOption
        };
        listCommand.Aliases.Add("ls");
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var clusterReference = parseResult.GetValue(listClusterOption);
            var databaseName = parseResult.GetValue(listDatabaseOption);
            var filterValue = parseResult.GetValue(filterOption);
            var takeValue = parseResult.GetValue(takeOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);
                var query = ListQueryBuilder.Build(".show tables | project TableName", "TableName", filterValue, takeValue);

                var result = await runtime.KustoService.ExecuteManagementCommandAsync(
                    resolvedCluster.Url,
                    resolvedDatabase,
                    query.Command,
                    query.Parameters,
                    ct);

                return new CliOutput
                {
                    Table = result,
                    IsQueryResultTable = true
                };
            }, cancellationToken);
        });

        var showCommand = new Command("show", "Show table schema and column details.")
        {
            showTableArgument,
            showClusterOption,
            showDatabaseOption,
            refreshOfflineDataOption
        };
        showCommand.Aliases.Add("get");
        showCommand.Aliases.Add("schema");
        showCommand.SetAction((parseResult, cancellationToken) =>
        {
            var tableName = parseResult.GetRequiredValue(showTableArgument);
            var clusterReference = parseResult.GetValue(showClusterOption);
            var databaseName = parseResult.GetValue(showDatabaseOption);
            var refreshOfflineData = parseResult.GetValue(refreshOfflineDataOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);
                var details = await runtime.TableSchemaProvider.GetTableSchemaDetailsAsync(
                    config,
                    resolvedCluster.Url,
                    resolvedDatabase,
                    tableName,
                    refreshOfflineData,
                    ct);

                return new CliOutput
                {
                    Message = details.NotesMessage,
                    Properties = details.Properties,
                    Table = details.Columns
                };
            }, cancellationToken);
        });

        var notesCommand = new Command("notes", "Manage extra notes associated with a table.")
        {
            notesTableArgument,
            notesClusterOption,
            notesDatabaseOption,
            addNoteOption,
            noteIdOption,
            deleteNoteOption,
            clearNotesOption,
            notesForceOption
        };
        notesCommand.SetAction((parseResult, cancellationToken) =>
        {
            var tableName = parseResult.GetValue(notesTableArgument);
            var clusterReference = parseResult.GetValue(notesClusterOption);
            var databaseName = parseResult.GetValue(notesDatabaseOption);
            var addNote = parseResult.GetValue(addNoteOption);
            var noteId = parseResult.GetValue(noteIdOption);
            var deleteNote = parseResult.GetValue(deleteNoteOption);
            var clearNotes = parseResult.GetValue(clearNotesOption);
            var force = parseResult.GetValue(notesForceOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var selectedActionCount = CountSpecified(
                    addNote is not null,
                    noteId is not null,
                    deleteNote is not null,
                    clearNotes);

                if (selectedActionCount > 1)
                {
                    throw new UserFacingException("Specify only one notes action at a time.");
                }

                if (force && !clearNotes)
                {
                    throw new UserFacingException("--force can only be used with --clear.");
                }

                if (selectedActionCount == 0 && string.IsNullOrWhiteSpace(tableName))
                {
                    throw new UserFacingException("Specify a table name or use --clear to clear all table notes.");
                }

                if (string.IsNullOrWhiteSpace(tableName))
                {
                    if (addNote is not null || noteId is not null || deleteNote is not null)
                    {
                        throw new UserFacingException("A table name is required for --add, --id, and --delete.");
                    }

                    if (!clearNotes)
                    {
                        throw new UserFacingException("Specify a table name or use --clear to clear all table notes.");
                    }

                    if (!string.IsNullOrWhiteSpace(clusterReference) || !string.IsNullOrWhiteSpace(databaseName))
                    {
                        throw new UserFacingException("--cluster and --database can only be used when a table name is provided.");
                    }
                }

                var config = await runtime.ConfigStore.LoadAsync(ct);

                if (clearNotes && string.IsNullOrWhiteSpace(tableName))
                {
                    if (!force && !await runtime.ConfirmationPrompt.ConfirmAsync("Clear all table notes?", ct))
                    {
                        return new CliOutput { Message = "Operation cancelled." };
                    }

                    return await runtime.TableOfflineDataManager.ClearTableNotesAsync(config, null, null, null, ct);
                }

                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);

                if (addNote is not null)
                {
                    return await runtime.TableOfflineDataManager.AddTableNoteAsync(
                        config,
                        resolvedCluster.Url,
                        resolvedDatabase,
                        tableName!,
                        addNote,
                        ct);
                }

                if (noteId is not null)
                {
                    return await runtime.TableOfflineDataManager.ShowTableNotesAsync(
                        config,
                        resolvedCluster.Url,
                        resolvedDatabase,
                        tableName!,
                        noteId,
                        ct);
                }

                if (deleteNote is not null)
                {
                    return await runtime.TableOfflineDataManager.DeleteTableNoteAsync(
                        config,
                        resolvedCluster.Url,
                        resolvedDatabase,
                        tableName!,
                        deleteNote.Value,
                        ct);
                }

                if (clearNotes)
                {
                    if (!force && !await runtime.ConfirmationPrompt.ConfirmAsync($"Clear all notes for table '{tableName}'?", ct))
                    {
                        return new CliOutput { Message = "Operation cancelled." };
                    }

                    return await runtime.TableOfflineDataManager.ClearTableNotesAsync(
                        config,
                        resolvedCluster.Url,
                        resolvedDatabase,
                        tableName,
                        ct);
                }

                return await runtime.TableOfflineDataManager.ShowTableNotesAsync(
                    config,
                    resolvedCluster.Url,
                    resolvedDatabase,
                    tableName!,
                    null,
                    ct);
            }, cancellationToken);
        });

        tableCommand.Add(listCommand);
        tableCommand.Add(showCommand);
        tableCommand.Add(notesCommand);
        return tableCommand;
    }

    private static Command BuildQueryCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var queryCommand = new Command("query", "Run KQL from inline text, --file/-f, or stdin against the selected cluster and database.");
        queryCommand.Aliases.Add("run");
        queryCommand.Aliases.Add("exec");

        var queryArgument = new Argument<string?>("query")
        {
            Description = "Inline KQL text, or '-' to read KQL from stdin.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryFileOption = CreateQueryFileOption();

        var clusterOption = CreateClusterOption();

        var databaseOption = CreateDatabaseOption();
        var showStatsOption = new Option<bool>("--show-stats")
        {
            Description = "Include query execution statistics when Kusto returns them."
        };
        var chartOption = new Option<bool>("--chart")
        {
            Description = "Render compatible query charts in the terminal for human output or as Mermaid for markdown output."
        };
        var outputChartOption = new Option<string?>("--output-chart")
        {
            Description = "Write the rendered chart to a PNG file at the given path. Works with any --format. For human/markdown/json suppresses raw data on stdout and emits a chart-written confirmation. For csv keeps the CSV on stdout and writes the confirmation to stderr."
        };
        var outputChartWidthOption = new Option<int>("--output-chart-width")
        {
            Description = $"PNG width in pixels for --output-chart (default {ChartStyle.DefaultWidth}, range {ChartStyle.MinDimension}..{ChartStyle.MaxDimension}).",
            DefaultValueFactory = _ => ChartStyle.DefaultWidth
        };
        var outputChartHeightOption = new Option<int>("--output-chart-height")
        {
            Description = $"PNG height in pixels for --output-chart (default {ChartStyle.DefaultHeight}, range {ChartStyle.MinDimension}..{ChartStyle.MaxDimension}).",
            DefaultValueFactory = _ => ChartStyle.DefaultHeight
        };

        queryCommand.Add(queryArgument);
        queryCommand.Add(queryFileOption);
        queryCommand.Add(clusterOption);
        queryCommand.Add(databaseOption);
        queryCommand.Add(showStatsOption);
        queryCommand.Add(chartOption);
        queryCommand.Add(outputChartOption);
        queryCommand.Add(outputChartWidthOption);
        queryCommand.Add(outputChartHeightOption);
        queryCommand.SetAction((parseResult, cancellationToken) =>
        {
            var queryText = parseResult.GetValue(queryArgument);
            var queryFile = parseResult.GetValue(queryFileOption);
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
            var showStats = parseResult.GetValue(showStatsOption);
            var showChart = parseResult.GetValue(chartOption);
            var chartOutputPath = parseResult.GetValue(outputChartOption);
            var chartWidth = parseResult.GetValue(outputChartWidthOption);
            var chartHeight = parseResult.GetValue(outputChartHeightOption);
            var chartWidthSpecified = parseResult.GetResult(outputChartWidthOption) is { Implicit: false };
            var chartHeightSpecified = parseResult.GetResult(outputChartHeightOption) is { Implicit: false };
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var isJsonOutput = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
                var isMarkdownOutput = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(format, "md", StringComparison.OrdinalIgnoreCase);
                var isCsvOutput = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
                if (showChart && (isJsonOutput || isCsvOutput))
                {
                    throw new UserFacingException($"--chart can't be used with --format {format.ToLowerInvariant()}.");
                }

                if (showStats && isCsvOutput)
                {
                    throw new UserFacingException("--show-stats can't be used with --format csv.");
                }

                if (string.IsNullOrEmpty(chartOutputPath) && (chartWidthSpecified || chartHeightSpecified))
                {
                    throw new UserFacingException("--output-chart-width and --output-chart-height require --output-chart.");
                }

                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);
                var query = await QueryTextResolver.ResolveAsync(
                    queryText,
                    queryFile,
                    Console.IsInputRedirected,
                    Console.In,
                    ct);
                QueryValidator.Validate(query);

                var result = await runtime.KustoService.ExecuteQueryAsync(
                    resolvedCluster.Url,
                    resolvedDatabase,
                    query,
                    showStats,
                    ct);

                string? chartHint = null;
                string? chartMessage = null;
                string? humanChart = null;
                string? humanChartAnsi = null;
                string? markdownChart = null;
                string? chartOutputWritten = null;
                if (result.Visualization is not null)
                {
                    var compatibility = KustoChartCompatibilityAnalyzer.Analyze(result.Table, result.Visualization);
                    if (showChart)
                    {
                        if (isMarkdownOutput)
                        {
                            if (compatibility.MarkdownChart is not null)
                            {
                                markdownChart = MermaidChartRenderer.Render(compatibility.MarkdownChart);
                            }
                            else
                            {
                                chartMessage = compatibility.MarkdownReason;
                            }
                        }
                        else
                        {
                            if (compatibility.HumanChart is not null)
                            {
                                var renderedChart = await Hex1bChartRenderer.RenderAsync(compatibility.HumanChart, ct);
                                humanChart = renderedChart.PlainText;
                                humanChartAnsi = renderedChart.AnsiText;
                            }
                            else
                            {
                                chartMessage = compatibility.HumanReason;
                            }
                        }
                    }
                    else if (!isMarkdownOutput && !isCsvOutput && compatibility.HumanChart is not null && string.IsNullOrEmpty(chartOutputPath))
                    {
                        chartHint = "This query can be rendered as a terminal chart. Re-run with --chart to see it.";
                    }

                    if (!string.IsNullOrEmpty(chartOutputPath))
                    {
                        if (compatibility.HumanChart is null)
                        {
                            throw new UserFacingException(compatibility.HumanReason ?? "This query's render kind isn't supported for image output.");
                        }

                        chartOutputWritten = await ChartImageWriter.WritePngAsync(
                            compatibility.HumanChart,
                            chartOutputPath!,
                            chartWidth,
                            chartHeight,
                            ct);

                        // CSV output is a pure data stream meant for redirection (e.g. `> data.csv`).
                        // Surface the chart confirmation on stderr only so it doesn't pollute the
                        // CSV stream. For human/markdown/json the confirmation flows through
                        // CliOutput.ChartOutputPath via the formatter.
                        if (isCsvOutput)
                        {
                            Console.Error.WriteLine($"Chart written to {chartOutputWritten}");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(chartOutputPath))
                {
                    throw new UserFacingException("--output-chart requires the query to include a 'render' annotation.");
                }

                // When --output-chart writes a PNG, suppress the tabular data from stdout for
                // display formats (human/markdown/json) — the chart already conveys it. Keep
                // the table for CSV so `--format csv --output-chart x.png > data.csv` produces
                // both the PNG (side effect) and the CSV stream (stdout). The chart-written
                // confirmation goes to stderr in that case (see above).
                var suppressTable = !string.IsNullOrEmpty(chartOutputPath) && !isCsvOutput;
                return new CliOutput
                {
                    Table = suppressTable ? null : result.Table,
                    WebExplorerUrl = result.WebExplorerUrl,
                    Statistics = result.Statistics,
                    Visualization = result.Visualization,
                    ChartHint = chartHint,
                    ChartMessage = chartMessage,
                    HumanChart = humanChart,
                    HumanChartAnsi = humanChartAnsi,
                    MarkdownChart = markdownChart,
                    ChartOutputPath = chartOutputWritten,
                    IsQueryResultTable = true
                };
            }, cancellationToken, OutputFormat.Human, OutputFormat.Json, OutputFormat.Markdown, OutputFormat.Csv);
        });

        return queryCommand;
    }

    private static Option<string?> CreateClusterOption()
    {
        return new Option<string?>("--cluster")
        {
            Description = "Saved cluster name or cluster URL to use. If omitted, the active/default cluster is used."
        };
    }

    private static Option<string?> CreateDatabaseOption()
    {
        var option = new Option<string?>("--database")
        {
            Description = "Database name to use. If omitted, the default database for the selected cluster is used."
        };
        option.Aliases.Add("--db");
        return option;
    }

    private static Option<string?> CreateFilterOption(string itemName)
    {
        return new Option<string?>("--filter")
        {
            Description = $"Filter by {itemName} name. Supports plain text, ^prefix, suffix$, or ^exact$."
        };
    }

    private static Option<int?> CreateTakeOption(string itemName)
    {
        var option = new Option<int?>("--take")
        {
            Description = $"Maximum number of {itemName} to return. Alias: --limit."
        };
        option.Aliases.Add("--limit");
        return option;
    }

    private static Option<string?> CreateQueryFileOption()
    {
        var option = new Option<string?>("--file")
        {
            Description = "Path to a file containing KQL query text. Append :<start>-<end> to read specific lines. Alias: -f."
        };
        option.Aliases.Add("-f");
        return option;
    }

    private static Option<FileInfo?> CreateOfflineDataFileOption(string name, string description)
    {
        return new Option<FileInfo?>(name)
        {
            Description = description
        };
    }

    private static Option<bool> CreateForceOption(string description)
    {
        var option = new Option<bool>("--force")
        {
            Description = $"{description} Alias: -f."
        };
        option.Aliases.Add("-f");
        return option;
    }

    private static int GetPreferredColumnIndex(TabularData table, string preferredColumnName)
    {
        if (table.TryGetColumnIndex(preferredColumnName, out var index))
        {
            return index;
        }

        return table.Columns.Count > 0 ? 0 : -1;
    }

    private static Dictionary<string, string?> ConvertRowToProperties(TabularData table, int rowIndex)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (rowIndex >= table.Rows.Count)
        {
            return properties;
        }

        var row = table.Rows[rowIndex];
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var value = i < row.Count ? row[i] : null;
            properties[table.Columns[i]] = value;
        }

        return properties;
    }

    private static string EscapeKustoLiteral(string input)
    {
        return KustoCommandText.EscapeSingleQuotedLiteral(input);
    }

    private static int CountSpecified(params bool[] values)
    {
        return values.Count(value => value);
    }

    private static void EnsureNoScopedTableOptions(string? tableName, string? clusterReference, string? databaseName, string optionName)
    {
        if (!string.IsNullOrWhiteSpace(tableName) ||
            !string.IsNullOrWhiteSpace(clusterReference) ||
            !string.IsNullOrWhiteSpace(databaseName))
        {
            throw new UserFacingException($"{optionName} can't be combined with a table name, --cluster, or --database.");
        }
    }
}

