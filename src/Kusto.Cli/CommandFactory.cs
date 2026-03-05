using System.CommandLine;

namespace Kusto.Cli;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand()
    {
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: human, json, markdown, md.",
            Recursive = true,
            DefaultValueFactory = _ => "human"
        };
        formatOption.AcceptOnlyFromAmong("human", "json", "markdown", "md");

        var logLevelOption = new Option<string?>("--log-level")
        {
            Description = "Set log level (Trace, Debug, Information, Warning, Error, Critical, None).",
            Recursive = true
        };

        var root = new RootCommand("A native command-line tool for Azure Data Explorer (Kusto).");
        root.Add(formatOption);
        root.Add(logLevelOption);
        root.Add(BuildClusterCommand(formatOption, logLevelOption));
        root.Add(BuildDatabaseCommand(formatOption, logLevelOption));
        root.Add(BuildTableCommand(formatOption, logLevelOption));
        root.Add(BuildQueryCommand(formatOption, logLevelOption));
        return root;
    }

    private static Command BuildClusterCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var clusterCommand = new Command("cluster", "Manage known clusters.");

        var listCommand = new Command("list", "List known clusters.");
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
            Description = "Cluster name or URL."
        };

        var showCommand = new Command("show", "Show a known cluster.");
        showCommand.Add(clusterReferenceArgument);
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

        var addCommand = new Command("add", "Add a known cluster.");
        var clusterNameArgument = new Argument<string>("name") { Description = "Friendly cluster name." };
        var clusterUrlArgument = new Argument<string>("url") { Description = "Cluster URL." };
        addCommand.Add(clusterNameArgument);
        addCommand.Add(clusterUrlArgument);
        addCommand.SetAction((parseResult, cancellationToken) =>
        {
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);
            var name = parseResult.GetRequiredValue(clusterNameArgument);
            var url = parseResult.GetRequiredValue(clusterUrlArgument);

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

                if (string.IsNullOrWhiteSpace(config.DefaultClusterUrl))
                {
                    config.DefaultClusterUrl = normalizedUrl;
                }

                await runtime.ConfigStore.SaveAsync(config, ct);
                return new CliOutput { Message = $"Added cluster '{name}' ({normalizedUrl})." };
            }, cancellationToken);
        });

        var removeCommand = new Command("remove", "Remove a known cluster.");
        removeCommand.Add(clusterReferenceArgument);
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

        var setDefaultCommand = new Command("set-default", "Set the default cluster.");
        setDefaultCommand.Add(clusterReferenceArgument);
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
        var clusterOption = new Option<string?>("--cluster")
        {
            Description = "Cluster name or URL to use."
        };

        var databaseCommand = new Command("database", "Manage databases and defaults.");

        var listCommand = new Command("list", "List databases.");
        listCommand.Add(clusterOption);
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var clusterReference = parseResult.GetValue(clusterOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);

                var databases = await runtime.KustoService.ExecuteManagementCommandAsync(
                    resolvedCluster.Url,
                    null,
                    ".show databases | project DatabaseName",
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

        var showCommand = new Command("show", "Show a database.");
        showCommand.Add(databaseArgument);
        showCommand.Add(clusterOption);
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
                var result = await runtime.KustoService.ExecuteManagementCommandAsync(resolvedCluster.Url, null, command, ct);

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

        var setDefaultCommand = new Command("set-default", "Set default database for a cluster.");
        setDefaultCommand.Add(databaseArgument);
        setDefaultCommand.Add(clusterOption);
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
                var result = await runtime.KustoService.ExecuteManagementCommandAsync(resolvedCluster.Url, null, verifyCommand, ct);
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
        var clusterOption = new Option<string?>("--cluster")
        {
            Description = "Cluster name or URL to use."
        };

        var databaseOption = new Option<string?>("--database")
        {
            Description = "Database name to use."
        };

        var tableCommand = new Command("table", "Browse tables and schemas.");

        var listCommand = new Command("list", "List tables in a database.");
        listCommand.Add(clusterOption);
        listCommand.Add(databaseOption);
        listCommand.SetAction((parseResult, cancellationToken) =>
        {
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
            var format = parseResult.GetRequiredValue(formatOption);
            var logLevel = parseResult.GetValue(logLevelOption);

            return CliRunner.RunAsync(format, logLevel, async (runtime, ct) =>
            {
                var config = await runtime.ConfigStore.LoadAsync(ct);
                var resolvedCluster = runtime.ConnectionResolver.ResolveCluster(config, clusterReference);
                var resolvedDatabase = runtime.ConnectionResolver.ResolveDatabase(config, resolvedCluster.Url, databaseName);

                var result = await runtime.KustoService.ExecuteManagementCommandAsync(
                    resolvedCluster.Url,
                    resolvedDatabase,
                    ".show tables | project TableName",
                    ct);

                return new CliOutput { Table = result };
            }, cancellationToken);
        });

        var tableArgument = new Argument<string>("table")
        {
            Description = "Table name."
        };

        var showCommand = new Command("show", "Show table schema.");
        showCommand.Add(tableArgument);
        showCommand.Add(clusterOption);
        showCommand.Add(databaseOption);
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

                var command = $".show table ['{EscapeKustoLiteral(tableName)}'] schema as json";
                var result = await runtime.KustoService.ExecuteManagementCommandAsync(
                    resolvedCluster.Url,
                    resolvedDatabase,
                    command,
                    ct);

                if (result.Rows.Count == 0)
                {
                    throw new UserFacingException($"Table '{tableName}' was not found.");
                }

                return new CliOutput
                {
                    Properties = ConvertRowToProperties(result, 0)
                };
            }, cancellationToken);
        });

        tableCommand.Add(listCommand);
        tableCommand.Add(showCommand);
        return tableCommand;
    }

    private static Command BuildQueryCommand(Option<string> formatOption, Option<string?> logLevelOption)
    {
        var queryCommand = new Command("query", "Run a KQL query from argument, --file, or stdin.");

        var queryArgument = new Argument<string?>("query")
        {
            Description = "Inline query text or '-' for stdin.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var queryFileOption = new Option<FileInfo?>("--file")
        {
            Description = "Path to a file containing KQL query text."
        };

        var clusterOption = new Option<string?>("--cluster")
        {
            Description = "Cluster name or URL to use."
        };

        var databaseOption = new Option<string?>("--database")
        {
            Description = "Database name to use."
        };

        queryCommand.Add(queryArgument);
        queryCommand.Add(queryFileOption);
        queryCommand.Add(clusterOption);
        queryCommand.Add(databaseOption);
        queryCommand.SetAction((parseResult, cancellationToken) =>
        {
            var queryText = parseResult.GetValue(queryArgument);
            var queryFile = parseResult.GetValue(queryFileOption);
            var clusterReference = parseResult.GetValue(clusterOption);
            var databaseName = parseResult.GetValue(databaseOption);
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
                    ct);

                return new CliOutput { Table = result };
            }, cancellationToken);
        });

        return queryCommand;
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
        return input.Replace("'", "''", StringComparison.Ordinal);
    }
}
