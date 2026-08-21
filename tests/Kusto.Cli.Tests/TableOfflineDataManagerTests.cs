using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class TableOfflineDataManagerTests
{
    [Fact]
    public async Task AddTableNoteAsync_ThenShowTableNotesAsync_ReturnsSequentialIds()
    {
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var manager = CreateManager(_ => throw new InvalidOperationException("Unexpected command."), cacheDirectory);
            var config = CreateConfig(cacheDirectory);

            _ = await manager.AddTableNoteAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", "First note", CancellationToken.None);
            _ = await manager.AddTableNoteAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", "Second note", CancellationToken.None);

            var output = await manager.ShowTableNotesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", null, CancellationToken.None);

            Assert.NotNull(output.Table);
            Assert.Equal(["Id", "Note"], output.Table.Columns);
            Assert.Equal(2, output.Table.Rows.Count);
            Assert.Equal("1", output.Table.Rows[0][0]);
            Assert.Equal("First note", output.Table.Rows[0][1]);
            Assert.Equal("2", output.Table.Rows[1][0]);
            Assert.Equal("Second note", output.Table.Rows[1][1]);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task ClearOfflineDataAsync_WhenSpecificTableCleared_RemovesEntry()
    {
        const string clusterUrl = "https://help.kusto.windows.net";
        const string database = "Samples";
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var store = CreateStore();
            var config = CreateConfig(cacheDirectory);
            await store.WriteEntryAsync(
                config,
                new DatabaseSchemaCacheEntry
                {
                    CacheFormatVersion = 1,
                    ClusterUrl = clusterUrl,
                    DatabaseName = database,
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    SchemaVersion = "v1.1",
                    SchemaJson = CreateDatabaseSchemaJson(database, "StormEvents"),
                    TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["StormEvents"] = ["Remember this table"]
                    }
                },
                CancellationToken.None);

            var manager = CreateManager(_ => throw new InvalidOperationException("Unexpected command."), cacheDirectory);
            var output = await manager.ClearOfflineDataAsync(config, clusterUrl, database, "StormEvents", CancellationToken.None);
            var entry = await store.TryReadEntryAsync(config, clusterUrl, database, CancellationToken.None);

            Assert.Equal("Cleared offline data for table 'StormEvents'.", output.Message);
            Assert.Null(entry);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task ExportOfflineDataAsync_AndImportOfflineDataAsync_RoundTripsEntries()
    {
        var sourceCacheDirectory = CreateTemporaryDirectory();
        var targetCacheDirectory = CreateTemporaryDirectory();
        var exportPath = Path.Combine(Path.GetTempPath(), $"kusto-offline-data-{Guid.NewGuid():N}.json");

        try
        {
            var store = CreateStore();
            var sourceConfig = CreateConfig(sourceCacheDirectory);
            await store.WriteEntryAsync(
                sourceConfig,
                new DatabaseSchemaCacheEntry
                {
                    CacheFormatVersion = 1,
                    ClusterUrl = "https://help.kusto.windows.net",
                    DatabaseName = "Samples",
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    SchemaVersion = "v1.1",
                    SchemaJson = CreateDatabaseSchemaJson("Samples", "StormEvents"),
                    TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["StormEvents"] = ["Exported note"]
                    }
                },
                CancellationToken.None);

            var sourceManager = CreateManager(_ => throw new InvalidOperationException("Unexpected command."), sourceCacheDirectory);
            var exportOutput = await sourceManager.ExportOfflineDataAsync(sourceConfig, exportPath, CancellationToken.None);

            var targetConfig = CreateConfig(targetCacheDirectory);
            var targetManager = CreateManager(_ => throw new InvalidOperationException("Unexpected command."), targetCacheDirectory);
            var importOutput = await targetManager.ImportOfflineDataAsync(targetConfig, exportPath, CancellationToken.None);
            var importedEntry = await CreateStore().TryReadEntryAsync(targetConfig, "https://help.kusto.windows.net", "Samples", CancellationToken.None);

            Assert.Contains("Exported 1 offline data entries", exportOutput.Message, StringComparison.Ordinal);
            Assert.Contains("Imported 1 offline data entries", importOutput.Message, StringComparison.Ordinal);
            Assert.NotNull(importedEntry);
            Assert.Equal("v1.1", importedEntry.SchemaVersion);
            Assert.Equal("Exported note", importedEntry.TableNotes["StormEvents"][0]);
        }
        finally
        {
            DeleteDirectory(sourceCacheDirectory);
            DeleteDirectory(targetCacheDirectory);
            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }

    [Fact]
    public async Task PurgeOfflineDataAsync_RemovesMissingTablesAndAssociatedNotes()
    {
        const string clusterUrl = "https://help.kusto.windows.net";
        const string database = "Samples";
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var store = CreateStore();
            var config = CreateConfig(cacheDirectory);
            await store.WriteEntryAsync(
                config,
                new DatabaseSchemaCacheEntry
                {
                    CacheFormatVersion = 1,
                    ClusterUrl = clusterUrl,
                    DatabaseName = database,
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    SchemaVersion = "v1.1",
                    SchemaJson = CreateMultiTableSchemaJson(database),
                    TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["StormEvents"] = ["Remove me"],
                        ["OtherTable"] = ["Keep me"]
                    }
                },
                CancellationToken.None);

            var manager = CreateManager(
                command =>
                {
                    Assert.Equal(".show tables | project TableName", command);
                    return new TabularData(["TableName"], [["OtherTable"]]);
                },
                cacheDirectory);

            var output = await manager.PurgeOfflineDataAsync(config, CancellationToken.None);
            var entry = await store.TryReadEntryAsync(config, clusterUrl, database, CancellationToken.None);

            Assert.Contains("Purged offline data for 1 tables", output.Message, StringComparison.Ordinal);
            Assert.NotNull(entry);
            Assert.DoesNotContain("StormEvents", DatabaseSchemaJson.GetTableNames(entry.SchemaJson, database), StringComparer.OrdinalIgnoreCase);
            Assert.False(entry.TableNotes.ContainsKey("StormEvents"));
            Assert.True(entry.TableNotes.ContainsKey("OtherTable"));
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    private static TableOfflineDataManager CreateManager(Func<string, TabularData> managementCommandHandler, string cacheDirectory)
    {
        return new TableOfflineDataManager(
            new RecordingKustoService(managementCommandHandler),
            CreateStore(),
            NullLogger<TableOfflineDataManager>.Instance);
    }

    private static OfflineTableDataStore CreateStore()
    {
        var resolver = new SchemaCacheSettingsResolver();
        return new OfflineTableDataStore(resolver, NullLogger<OfflineTableDataStore>.Instance);
    }

    private static KustoConfig CreateConfig(string cacheDirectory)
    {
        return new KustoConfig
        {
            SchemaCache = new SchemaCacheConfig
            {
                Enabled = true,
                Path = cacheDirectory
            }
        };
    }

    private static string CreateDatabaseSchemaJson(string database, string tableName)
    {
        return $$"""
            {
              "Databases": {
                "{{database}}": {
                  "Name": "{{database}}",
                  "Tables": {
                    "{{tableName}}": {
                      "Name": "{{tableName}}",
                      "OrderedColumns": [{"Name":"State","Type":"System.String"}]
                    }
                  },
                  "MajorVersion": 1,
                  "MinorVersion": 1
                }
              }
            }
            """;
    }

    private static string CreateMultiTableSchemaJson(string database)
    {
        return $$"""
            {
              "Databases": {
                "{{database}}": {
                  "Name": "{{database}}",
                  "Tables": {
                    "StormEvents": {
                      "Name": "StormEvents",
                      "OrderedColumns": [{"Name":"State","Type":"System.String"}]
                    },
                    "OtherTable": {
                      "Name": "OtherTable",
                      "OrderedColumns": [{"Name":"Value","Type":"System.String"}]
                    }
                  },
                  "MajorVersion": 1,
                  "MinorVersion": 1
                }
              }
            }
            """;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kusto-offline-data-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingKustoService(Func<string, TabularData> managementCommandHandler) : IKustoService
    {
        private readonly Func<string, TabularData> _managementCommandHandler = managementCommandHandler;

        public Task<TabularData> ExecuteManagementCommandAsync(
            ResolvedCluster cluster,
            string? database,
            string command,
            IReadOnlyDictionary<string, string>? queryParameters,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_managementCommandHandler(command));
        }

        public Task<QueryExecutionResult> ExecuteQueryAsync(
            ResolvedCluster cluster,
            string database,
            string query,
            bool includeStatistics,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
