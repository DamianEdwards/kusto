using System.CommandLine;

namespace Kusto.Cli;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand()
    {
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format for people or tools: human, json, markdown, md.",
            Recursive = true,
            DefaultValueFactory = _ => "human"
        };
        formatOption.AcceptOnlyFromAmong("human", "json", "markdown", "md");

        var logLevelOption = new Option<string?>("--log-level")
        {
            Description = "Console log level (Trace, Debug, Information, Warning, Error, Critical, None).",
            Recursive = true
        };

        var root = new RootCommand("Query Azure Data Explorer (Kusto) from the terminal: save clusters, pick defaults, inspect databases and tables, and run KQL.")
        {
            formatOption,
            logLevelOption,
            BuildExamplesCommand(formatOption, logLevelOption),
            BuildClusterCommand(formatOption, logLevelOption),
            BuildDatabaseCommand(formatOption, logLevelOption),
            BuildTableCommand(formatOption, logLevelOption),
            BuildQueryCommand(formatOption, logLevelOption)
        };
        return root;
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
        var clusterOption = CreateClusterOption();

        var databaseOption = CreateDatabaseOption();
        var filterOption = CreateFilterOption("table");
        var takeOption = CreateTakeOption("tables");

        var tableCommand = new Command("table", "Browse tables and inspect schema.");
        tableCommand.Aliases.Add("tables");

        var listCommand = new Command("list", "List tables in a database. Use --filter or --limit to narrow results.")
        {
            clusterOption,
            databaseOption,
            filterOption,
            takeOption
        };
        listCommand.Aliases.Add("ls");
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
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

        var tableArgument = new Argument<string>("table")
        {
            Description = "Table name."
        };

        var showCommand = new Command("show", "Show table schema and column details.")
        {
            tableArgument,
            clusterOption,
            databaseOption
        };
        showCommand.Aliases.Add("get");
        showCommand.Aliases.Add("schema");
        showCommand.SetAction((parseResult, cancellationToken) =>
        {
            var tableName = parseResult.GetRequiredValue(tableArgument);
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);

                return new CliOutput
                {
                    Properties = await runtime.TableSchemaProvider.GetTablePropertiesAsync(
                        config,
                        resolvedCluster.Url,
                        resolvedDatabase,
                        tableName,
                        ct)
                };
            }, cancellationToken);
        });

        tableCommand.Add(listCommand);
        tableCommand.Add(showCommand);
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

        queryCommand.Add(queryArgument);
        queryCommand.Add(queryFileOption);
        queryCommand.Add(clusterOption);
        queryCommand.Add(databaseOption);
        queryCommand.Add(showStatsOption);
        queryCommand.SetAction((parseResult, cancellationToken) =>
        {
            var queryText = parseResult.GetValue(queryArgument);
            var queryFile = parseResult.GetValue(queryFileOption);
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
            var showStats = parseResult.GetValue(showStatsOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);
                var query = await QueryTextResolver.ResolveAsync(
                    queryText,
                    queryFile?.FullName,
                    Console.IsInputRedirected,
                    Console.In,
                    ct);

                var result = await runtime.KustoService.ExecuteQueryAsync(
                    resolvedCluster.Url,
                    resolvedDatabase,
                    query,
                    showStats,
                    ct);

                return new CliOutput
                {
                    Table = result.Table,
                    WebExplorerUrl = result.WebExplorerUrl,
                    Statistics = result.Statistics,
                    IsQueryResultTable = true
                };
            }, cancellationToken);
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

    private static Option<FileInfo?> CreateQueryFileOption()
    {
        var option = new Option<FileInfo?>("--file")
        {
            Description = "Path to a file containing KQL query text. Alias: -f."
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
}
