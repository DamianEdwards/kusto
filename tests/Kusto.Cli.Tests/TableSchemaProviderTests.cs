using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class TableSchemaProviderTests
{
    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenCacheDisabled_UsesLiveDatabaseSchemaCommand()
    {
        var service = new RecordingKustoService((_, _, command) =>
        {
            Assert.Equal(".show database ['Samples'] schema as json", command);
            return CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                "Samples",
                "StormEvents",
                1,
                1,
                "Table level docstring",
                ("State", "System.String", "State docstring")));
        });
        var provider = CreateProvider(service, CreateTemporaryDirectory(), ttlSeconds: 300);

        var details = await provider.GetTableSchemaDetailsAsync(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = false
                }
            },
            new ResolvedCluster(null, "https://help.kusto.windows.net"),
            "Samples",
            "StormEvents",
            refreshOfflineData: false,
            CancellationToken.None);

        Assert.Equal("StormEvents", details.Properties["TableName"]);
        Assert.Equal("Table level docstring", details.Properties["DocString"]);
        Assert.Equal("1", details.Properties["ColumnCount"]);
        var row = Assert.Single(details.Columns.Rows);
        Assert.Equal("State docstring", row[3]);
        Assert.Single(service.ManagementCommands);
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenCacheEnabled_UsesFreshCacheOnSecondRead()
    {
        var service = new RecordingKustoService((_, _, _) => CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
            "Samples",
            "StormEvents",
            1,
            1,
            null,
            ("State", "System.String", null))));
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var config = CreateCacheEnabledConfig(cacheDirectory, 300);

            var first = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            var second = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            Assert.Equal(first.Columns.Rows.Count, second.Columns.Rows.Count);
            Assert.Single(service.ManagementCommands);
            Assert.Equal(".show database ['Samples'] schema as json", service.ManagementCommands[0]);
            Assert.Single(Directory.GetFiles(cacheDirectory));
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenCacheExpires_RevalidatesWithIfLaterThan()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-06T00:00:00Z"));
        var service = new RecordingKustoService((_, _, command) =>
        {
            if (command.Contains("if_later_than", StringComparison.Ordinal))
            {
                return new TabularData(["DatabaseSchema"], []);
            }

            return CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                "Samples",
                "StormEvents",
                1,
                1,
                null,
                ("State", "System.String", null)));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 60, timeProvider: timeProvider);
            var config = CreateCacheEnabledConfig(cacheDirectory, 60);

            _ = await provider.GetTableSchemaDetailsAsync(config, new ResolvedCluster(null, "https://help.kusto.windows.net"), "Samples", "StormEvents", false, CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromMinutes(2));

            _ = await provider.GetTableSchemaDetailsAsync(config, new ResolvedCluster(null, "https://help.kusto.windows.net"), "Samples", "StormEvents", false, CancellationToken.None);
            _ = await provider.GetTableSchemaDetailsAsync(config, new ResolvedCluster(null, "https://help.kusto.windows.net"), "Samples", "StormEvents", false, CancellationToken.None);

            Assert.Equal(2, service.ManagementCommands.Count);
            Assert.Equal(".show database ['Samples'] schema as json", service.ManagementCommands[0]);
            Assert.Equal(".show database ['Samples'] schema if_later_than \"v1.1\" as json", service.ManagementCommands[1]);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenCacheExpiresAndSchemaChanges_RefreshesCache()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-06T00:00:00Z"));
        var service = new RecordingKustoService((_, _, command) =>
        {
            if (command.Contains("if_later_than", StringComparison.Ordinal))
            {
                return CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                    "Samples",
                    "StormEvents",
                    1,
                    2,
                    null,
                    ("State", "System.String", null),
                    ("EventId", "System.Int64", null)));
            }

            return CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                "Samples",
                "StormEvents",
                1,
                1,
                null,
                ("State", "System.String", null)));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 60, timeProvider: timeProvider);
            var config = CreateCacheEnabledConfig(cacheDirectory, 60);

            var initial = await provider.GetTableSchemaDetailsAsync(config, new ResolvedCluster(null, "https://help.kusto.windows.net"), "Samples", "StormEvents", false, CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromMinutes(2));

            var refreshed = await provider.GetTableSchemaDetailsAsync(config, new ResolvedCluster(null, "https://help.kusto.windows.net"), "Samples", "StormEvents", false, CancellationToken.None);

            Assert.NotEqual(initial.Columns.Rows.Count, refreshed.Columns.Rows.Count);
            Assert.Equal(2, service.ManagementCommands.Count);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenCacheFileIsCorrupt_RefetchesSchema()
    {
        const string clusterUrl = "https://help.kusto.windows.net";
        const string database = "Samples";
        var cacheDirectory = CreateTemporaryDirectory();
        var cachePath = GetCachePath(cacheDirectory, clusterUrl, database);
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(cachePath, "{ not json");
        var service = new RecordingKustoService((_, _, _) => CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
            database,
            "StormEvents",
            1,
            1,
            null,
            ("State", "System.String", null))));

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var details = await provider.GetTableSchemaDetailsAsync(
                CreateCacheEnabledConfig(cacheDirectory, 300),
                new ResolvedCluster(null, clusterUrl),
                database,
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            Assert.Equal("StormEvents", details.Properties["TableName"]);
            Assert.Single(service.ManagementCommands);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenEmbeddedSchemaJsonIsMalformed_RefreshesSchema()
    {
        const string clusterUrl = "https://help.kusto.windows.net";
        const string database = "Samples";
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var store = new OfflineTableDataStore(new SchemaCacheSettingsResolver(), NullLogger<OfflineTableDataStore>.Instance);
            var config = CreateCacheEnabledConfig(cacheDirectory, 300);
            await store.WriteEntryAsync(
                config,
                new DatabaseSchemaCacheEntry
                {
                    CacheFormatVersion = 1,
                    ClusterUrl = clusterUrl,
                    DatabaseName = database,
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    SchemaVersion = "v1.1",
                    SchemaJson = "{ bad json",
                    TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                },
                CancellationToken.None);

            var service = new RecordingKustoService((_, _, _) => CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                database,
                "StormEvents",
                1,
                2,
                null,
                ("State", "System.String", null))));
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);

            var details = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, clusterUrl),
                database,
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            Assert.Equal("StormEvents", details.Properties["TableName"]);
            Assert.Single(service.ManagementCommands);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenFreshCacheDoesNotContainTable_RefreshesBeforeFailing()
    {
        var callCount = 0;
        var service = new RecordingKustoService((_, _, command) =>
        {
            callCount++;
            if (command.Contains("if_later_than", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected revalidation call.");
            }

            return callCount == 1
                ? CreateDatabaseSchemaResult(CreateDatabaseSchemaJson("Samples", "OtherTable", 1, 1, null, ("State", "System.String", null)))
                : CreateDatabaseSchemaResult(CreateDatabaseSchemaJson("Samples", "StormEvents", 1, 2, null, ("State", "System.String", null)));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var config = CreateCacheEnabledConfig(cacheDirectory, 300);

            var initial = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "OtherTable",
                refreshOfflineData: false,
                CancellationToken.None);

            var details = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            Assert.Equal("OtherTable", initial.Properties["TableName"]);
            Assert.Equal("StormEvents", details.Properties["TableName"]);
            Assert.Equal(2, service.ManagementCommands.Count);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_WhenRefreshOfflineDataRequested_WritesSchemaEvenWhenCacheDisabled()
    {
        var cacheDirectory = CreateTemporaryDirectory();
        var service = new RecordingKustoService((_, _, _) => CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
            "Samples",
            "StormEvents",
            1,
            1,
            null,
            ("State", "System.String", null))));

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var config = new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = false,
                    Path = cacheDirectory
                }
            };

            _ = await provider.GetTableSchemaDetailsAsync(
                config,
                new ResolvedCluster(null, "https://help.kusto.windows.net"),
                "Samples",
                "StormEvents",
                refreshOfflineData: true,
                CancellationToken.None);

            Assert.Single(Directory.GetFiles(cacheDirectory));
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTableSchemaDetailsAsync_IncludesStoredNotesInOutput()
    {
        const string clusterUrl = "https://help.kusto.windows.net";
        const string database = "Samples";
        var cacheDirectory = CreateTemporaryDirectory();
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = GetCachePath(cacheDirectory, clusterUrl, database);
        var entry = new DatabaseSchemaCacheEntry
        {
            CacheFormatVersion = 1,
            ClusterUrl = clusterUrl,
            DatabaseName = database,
            CachedAtUtc = DateTimeOffset.UtcNow,
            SchemaVersion = "v1.1",
            SchemaJson = CreateDatabaseSchemaJson(database, "StormEvents", 1, 1, "Table doc", ("State", "System.String", "State doc")),
            TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["StormEvents"] = ["Use for weather samples."]
            }
        };
        await using (var stream = File.Create(cachePath))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, entry, KustoJsonSerializerContext.Default.DatabaseSchemaCacheEntry);
        }

        try
        {
            var provider = CreateProvider(new RecordingKustoService((_, _, _) => throw new InvalidOperationException("Should use cache.")), cacheDirectory, ttlSeconds: 300);
            var details = await provider.GetTableSchemaDetailsAsync(
                CreateCacheEnabledConfig(cacheDirectory, 300),
                new ResolvedCluster(null, clusterUrl),
                database,
                "StormEvents",
                refreshOfflineData: false,
                CancellationToken.None);

            Assert.Equal("1", details.Properties["NoteCount"]);
            Assert.Contains("Use for weather samples.", details.NotesMessage, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    private static TableSchemaProvider CreateProvider(
        RecordingKustoService service,
        string cacheDirectory,
        int ttlSeconds,
        TimeProvider? timeProvider = null)
    {
        var resolver = new SchemaCacheSettingsResolver();
        var store = new OfflineTableDataStore(resolver, NullLogger<OfflineTableDataStore>.Instance);
        return new TableSchemaProvider(
            service,
            store,
            resolver,
            NullLogger<TableSchemaProvider>.Instance,
            timeProvider ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-03-06T00:00:00Z")));
    }

    private static KustoConfig CreateCacheEnabledConfig(string cacheDirectory, int ttlSeconds)
    {
        return new KustoConfig
        {
            SchemaCache = new SchemaCacheConfig
            {
                Enabled = true,
                Path = cacheDirectory,
                TtlSeconds = ttlSeconds
            }
        };
    }

    private static TabularData CreateDatabaseSchemaResult(string schemaJson)
    {
        return new TabularData(["DatabaseSchema"], [[schemaJson]]);
    }

    private static string CreateDatabaseSchemaJson(
        string database,
        string tableName,
        int majorVersion,
        int minorVersion,
        string? tableDocString,
        params (string Name, string Type, string? DocString)[] columns)
    {
        var orderedColumns = string.Join(
            ",",
            columns.Select(column =>
            {
                var docStringProperty = column.DocString is null
                    ? string.Empty
                    : $",\"DocString\":\"{column.DocString}\"";

                return $$"""{"Name":"{{column.Name}}","Type":"{{column.Type}}"{{docStringProperty}}}""";
            }));

        var tableDocStringProperty = tableDocString is null
            ? string.Empty
            : $",\"DocString\":\"{tableDocString}\"";

        return $$"""
            {
              "Databases": {
                "{{database}}": {
                  "Name": "{{database}}",
                  "Tables": {
                    "{{tableName}}": {
                      "Name": "{{tableName}}"{{tableDocStringProperty}},
                      "OrderedColumns": [{{orderedColumns}}]
                    }
                  },
                  "MajorVersion": {{majorVersion}},
                  "MinorVersion": {{minorVersion}}
                }
              }
            }
            """;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kusto-schema-cache-tests-{Guid.NewGuid():N}");
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

    private static string GetCachePath(string cacheDirectory, string clusterUrl, string database)
    {
        var keyBytes = Encoding.UTF8.GetBytes($"database-schema:v1:{clusterUrl.ToLowerInvariant()}|{database.ToLowerInvariant()}");
        var hash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        return Path.Combine(cacheDirectory, $"{hash}.json");
    }

    private sealed class RecordingKustoService(Func<string, string, string, TabularData> responseFactory) : IKustoService
    {
        private readonly Func<string, string, string, TabularData> _responseFactory = responseFactory;

        public List<string> ManagementCommands { get; } = [];

        public Task<TabularData> ExecuteManagementCommandAsync(
            ResolvedCluster cluster,
            string? database,
            string command,
            IReadOnlyDictionary<string, string>? queryParameters,
            CancellationToken cancellationToken)
        {
            ManagementCommands.Add(command);
            return Task.FromResult(_responseFactory(cluster.Url, database ?? string.Empty, command));
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

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
        }
    }
}
