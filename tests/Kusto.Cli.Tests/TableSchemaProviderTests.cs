using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class TableSchemaProviderTests
{
    [Fact]
    public async Task GetTablePropertiesAsync_WhenCacheDisabled_UsesLiveTableCommand()
    {
        var service = new RecordingKustoService((_, _, command) =>
        {
            Assert.Equal(".show table ['StormEvents'] schema as json", command);
            return CreateTableSchemaResult("Samples", "StormEvents", """[{"Name":"State","Type":"System.String"}]""");
        });
        var provider = new TableSchemaProvider(
            service,
            new SchemaCacheSettingsResolver(),
            NullLogger<TableSchemaProvider>.Instance,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-06T00:00:00Z")));

        var properties = await provider.GetTablePropertiesAsync(
            new KustoConfig
            {
                SchemaCache = new SchemaCacheConfig
                {
                    Enabled = false
                }
            },
            "https://help.kusto.windows.net",
            "Samples",
            "StormEvents",
            CancellationToken.None);

        Assert.Equal("StormEvents", properties["TableName"]);
        Assert.Equal("""[{"Name":"State","Type":"System.String"}]""", properties["Schema"]);
        Assert.Single(service.ManagementCommands);
    }

    [Fact]
    public async Task GetTablePropertiesAsync_WhenCacheEnabled_UsesFreshCacheOnSecondRead()
    {
        var service = new RecordingKustoService((_, _, _) => CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
            "Samples",
            "StormEvents",
            1,
            1,
            ("State", "System.String"))));
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var config = CreateCacheEnabledConfig(cacheDirectory, 300);

            var first = await provider.GetTablePropertiesAsync(
                config,
                "https://help.kusto.windows.net",
                "Samples",
                "StormEvents",
                CancellationToken.None);

            var second = await provider.GetTablePropertiesAsync(
                config,
                "https://help.kusto.windows.net",
                "Samples",
                "StormEvents",
                CancellationToken.None);

            Assert.Equal(first["Schema"], second["Schema"]);
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
    public async Task GetTablePropertiesAsync_WhenCacheExpires_RevalidatesWithIfLaterThan()
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
                ("State", "System.String")));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 60, timeProvider: timeProvider);
            var config = CreateCacheEnabledConfig(cacheDirectory, 60);

            _ = await provider.GetTablePropertiesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromMinutes(2));

            _ = await provider.GetTablePropertiesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", CancellationToken.None);
            _ = await provider.GetTablePropertiesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", CancellationToken.None);

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
    public async Task GetTablePropertiesAsync_WhenCacheExpiresAndSchemaChanges_RefreshesCache()
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
                    ("State", "System.String"),
                    ("EventId", "System.Int64")));
            }

            return CreateDatabaseSchemaResult(CreateDatabaseSchemaJson(
                "Samples",
                "StormEvents",
                1,
                1,
                ("State", "System.String")));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 60, timeProvider: timeProvider);
            var config = CreateCacheEnabledConfig(cacheDirectory, 60);

            var initial = await provider.GetTablePropertiesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromMinutes(2));

            var refreshed = await provider.GetTablePropertiesAsync(config, "https://help.kusto.windows.net", "Samples", "StormEvents", CancellationToken.None);

            Assert.NotEqual(initial["Schema"], refreshed["Schema"]);
            Assert.Contains("EventId", refreshed["Schema"], StringComparison.Ordinal);
            Assert.Equal(2, service.ManagementCommands.Count);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTablePropertiesAsync_WhenCacheFileIsCorrupt_RefetchesSchema()
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
            ("State", "System.String"))));

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var properties = await provider.GetTablePropertiesAsync(
                CreateCacheEnabledConfig(cacheDirectory, 300),
                clusterUrl,
                database,
                "StormEvents",
                CancellationToken.None);

            Assert.Equal("StormEvents", properties["TableName"]);
            Assert.Single(service.ManagementCommands);
        }
        finally
        {
            DeleteDirectory(cacheDirectory);
        }
    }

    [Fact]
    public async Task GetTablePropertiesAsync_WhenFreshCacheDoesNotContainTable_RefreshesBeforeFailing()
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
                ? CreateDatabaseSchemaResult(CreateDatabaseSchemaJson("Samples", "OtherTable", 1, 1, ("State", "System.String")))
                : CreateDatabaseSchemaResult(CreateDatabaseSchemaJson("Samples", "StormEvents", 1, 2, ("State", "System.String")));
        });
        var cacheDirectory = CreateTemporaryDirectory();

        try
        {
            var provider = CreateProvider(service, cacheDirectory, ttlSeconds: 300);
            var config = CreateCacheEnabledConfig(cacheDirectory, 300);

            var initial = await provider.GetTablePropertiesAsync(
                config,
                "https://help.kusto.windows.net",
                "Samples",
                "OtherTable",
                CancellationToken.None);

            var properties = await provider.GetTablePropertiesAsync(
                config,
                "https://help.kusto.windows.net",
                "Samples",
                "StormEvents",
                CancellationToken.None);

            Assert.Equal("OtherTable", initial["TableName"]);
            Assert.Equal("StormEvents", properties["TableName"]);
            Assert.Equal(2, service.ManagementCommands.Count);
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
        return new TableSchemaProvider(
            service,
            new SchemaCacheSettingsResolver(),
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

    private static TabularData CreateTableSchemaResult(string database, string tableName, string schemaJson)
    {
        return new TabularData(
            ["TableName", "Schema", "DatabaseName"],
            [[tableName, schemaJson, database]]);
    }

    private static TabularData CreateDatabaseSchemaResult(string schemaJson)
    {
        return new TabularData(["DatabaseSchema"], [[schemaJson]]);
    }

    private static string CreateDatabaseSchemaJson(string database, string tableName, int majorVersion, int minorVersion, params (string Name, string Type)[] columns)
    {
        var orderedColumns = string.Join(
            ",",
            columns.Select(column => $$"""{"Name":"{{column.Name}}","Type":"{{column.Type}}"}"""));

        return $$"""
            {
              "Databases": {
                "{{database}}": {
                  "Name": "{{database}}",
                  "Tables": {
                    "{{tableName}}": {
                      "Name": "{{tableName}}",
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
            string clusterUrl,
            string? database,
            string command,
            IReadOnlyDictionary<string, string>? queryParameters,
            CancellationToken cancellationToken)
        {
            ManagementCommands.Add(command);
            return Task.FromResult(_responseFactory(clusterUrl, database ?? string.Empty, command));
        }

        public Task<QueryExecutionResult> ExecuteQueryAsync(
            string clusterUrl,
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
