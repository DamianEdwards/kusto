using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public sealed class TableSchemaProvider(
    IKustoService kustoService,
    SchemaCacheSettingsResolver settingsResolver,
    ILogger<TableSchemaProvider> logger,
    TimeProvider? timeProvider = null) : ITableSchemaProvider
{
    private const int CacheFormatVersion = 1;

    private readonly IKustoService _kustoService = kustoService;
    private readonly SchemaCacheSettingsResolver _settingsResolver = settingsResolver;
    private readonly ILogger<TableSchemaProvider> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<Dictionary<string, string?>> GetTablePropertiesAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        CancellationToken cancellationToken)
    {
        var settings = _settingsResolver.Resolve(config, clusterUrl, database);
        return settings.Enabled
            ? GetCachedTablePropertiesAsync(settings, clusterUrl, database, tableName, cancellationToken)
            : GetLiveTablePropertiesAsync(clusterUrl, database, tableName, cancellationToken);
    }

    private async Task<Dictionary<string, string?>> GetCachedTablePropertiesAsync(
        ResolvedSchemaCacheSettings settings,
        string clusterUrl,
        string database,
        string tableName,
        CancellationToken cancellationToken)
    {
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var cachePath = BuildCachePath(settings.CacheDirectory, normalizedClusterUrl, database);
        var cacheEntry = await TryReadCacheEntryAsync(cachePath, cancellationToken);

        if (cacheEntry is not null && !IsExpired(cacheEntry, settings.Ttl))
        {
            _logger.LogDebug("Using cached schema for {ClusterUrl}/{Database}.", normalizedClusterUrl, database);
            try
            {
                return BuildPropertiesFromDatabaseSchema(cacheEntry.SchemaJson, database, tableName);
            }
            catch (UserFacingException ex)
            {
                _logger.LogDebug(ex, "Refreshing cached schema for {ClusterUrl}/{Database} after a cache lookup miss.", normalizedClusterUrl, database);
                var refreshedEntry = await FetchDatabaseSchemaAsync(normalizedClusterUrl, database, cancellationToken);
                await WriteCacheEntryAsync(cachePath, refreshedEntry, cancellationToken);
                return BuildPropertiesFromDatabaseSchema(refreshedEntry.SchemaJson, database, tableName);
            }
        }

        if (cacheEntry is not null)
        {
            cacheEntry = await RefreshCacheEntryAsync(cacheEntry, normalizedClusterUrl, database, cachePath, cancellationToken);
            return BuildPropertiesFromDatabaseSchema(cacheEntry.SchemaJson, database, tableName);
        }

        var fetchedEntry = await FetchDatabaseSchemaAsync(normalizedClusterUrl, database, cancellationToken);
        await WriteCacheEntryAsync(cachePath, fetchedEntry, cancellationToken);
        return BuildPropertiesFromDatabaseSchema(fetchedEntry.SchemaJson, database, tableName);
    }

    private async Task<DatabaseSchemaCacheEntry> RefreshCacheEntryAsync(
        DatabaseSchemaCacheEntry cacheEntry,
        string clusterUrl,
        string database,
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cacheEntry.SchemaVersion))
        {
            var refreshedEntry = await TryFetchDatabaseSchemaIfUpdatedAsync(clusterUrl, database, cacheEntry.SchemaVersion, cancellationToken);
            if (refreshedEntry is null)
            {
                cacheEntry.CachedAtUtc = _timeProvider.GetUtcNow();
                await WriteCacheEntryAsync(cachePath, cacheEntry, cancellationToken);
                return cacheEntry;
            }

            await WriteCacheEntryAsync(cachePath, refreshedEntry, cancellationToken);
            return refreshedEntry;
        }

        var fullRefresh = await FetchDatabaseSchemaAsync(clusterUrl, database, cancellationToken);
        await WriteCacheEntryAsync(cachePath, fullRefresh, cancellationToken);
        return fullRefresh;
    }

    private async Task<DatabaseSchemaCacheEntry> FetchDatabaseSchemaAsync(
        string clusterUrl,
        string database,
        CancellationToken cancellationToken)
    {
        var result = await _kustoService.ExecuteManagementCommandAsync(
            clusterUrl,
            database,
            BuildShowDatabaseSchemaCommand(database, null),
            null,
            cancellationToken);

        return CreateCacheEntry(clusterUrl, database, ExtractSchemaJson(result));
    }

    private async Task<DatabaseSchemaCacheEntry?> TryFetchDatabaseSchemaIfUpdatedAsync(
        string clusterUrl,
        string database,
        string schemaVersion,
        CancellationToken cancellationToken)
    {
        var result = await _kustoService.ExecuteManagementCommandAsync(
            clusterUrl,
            database,
            BuildShowDatabaseSchemaCommand(database, schemaVersion),
            null,
            cancellationToken);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        return CreateCacheEntry(clusterUrl, database, ExtractSchemaJson(result));
    }

    private async Task<Dictionary<string, string?>> GetLiveTablePropertiesAsync(
        string clusterUrl,
        string database,
        string tableName,
        CancellationToken cancellationToken)
    {
        var command = $".show table ['{KustoCommandText.EscapeSingleQuotedLiteral(tableName)}'] schema as json";
        var result = await _kustoService.ExecuteManagementCommandAsync(
            clusterUrl,
            database,
            command,
            null,
            cancellationToken);

        if (result.Rows.Count == 0)
        {
            throw new UserFacingException($"Table '{tableName}' was not found.");
        }

        return ConvertRowToProperties(result, 0);
    }

    private async Task<DatabaseSchemaCacheEntry?> TryReadCacheEntryAsync(string cachePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var cacheEntry = await JsonSerializer.DeserializeAsync(
                stream,
                KustoJsonSerializerContext.Default.DatabaseSchemaCacheEntry,
                cancellationToken);

            if (cacheEntry is null || cacheEntry.CacheFormatVersion != CacheFormatVersion)
            {
                return null;
            }

            return cacheEntry;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable schema cache entry at {CachePath}.", cachePath);
            return null;
        }
    }

    private async Task WriteCacheEntryAsync(string cachePath, DatabaseSchemaCacheEntry cacheEntry, CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            return;
        }

        var temporaryPath = Path.Combine(cacheDirectory, $"{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    cacheEntry,
                    KustoJsonSerializerContext.Default.DatabaseSchemaCacheEntry,
                    cancellationToken);
            }

            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to write schema cache entry to {CachePath}.", cachePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to clean up temporary schema cache file {TemporaryPath}.", temporaryPath);
            }
        }
    }

    private DatabaseSchemaCacheEntry CreateCacheEntry(string clusterUrl, string database, string schemaJson)
    {
        return new DatabaseSchemaCacheEntry
        {
            CacheFormatVersion = CacheFormatVersion,
            ClusterUrl = clusterUrl,
            DatabaseName = database,
            CachedAtUtc = _timeProvider.GetUtcNow(),
            SchemaVersion = ExtractDatabaseSchemaVersion(schemaJson, database),
            SchemaJson = schemaJson
        };
    }

    private static string BuildShowDatabaseSchemaCommand(string database, string? schemaVersion)
    {
        var escapedDatabase = KustoCommandText.EscapeSingleQuotedLiteral(database);
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return $".show database ['{escapedDatabase}'] schema as json";
        }

        var escapedSchemaVersion = KustoCommandText.EscapeDoubleQuotedLiteral(schemaVersion);
        return $".show database ['{escapedDatabase}'] schema if_later_than \"{escapedSchemaVersion}\" as json";
    }

    private static string BuildCachePath(string cacheDirectory, string clusterUrl, string database)
    {
        var keyBytes = Encoding.UTF8.GetBytes($"database-schema:v{CacheFormatVersion}:{clusterUrl.ToLowerInvariant()}|{database.ToLowerInvariant()}");
        var hash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        return Path.Combine(cacheDirectory, $"{hash}.json");
    }

    private bool IsExpired(DatabaseSchemaCacheEntry cacheEntry, TimeSpan ttl)
    {
        return _timeProvider.GetUtcNow() - cacheEntry.CachedAtUtc >= ttl;
    }

    private static string ExtractSchemaJson(TabularData result)
    {
        if (result.Rows.Count == 0)
        {
            throw new UserFacingException("Kusto did not return a database schema.");
        }

        var row = result.Rows[0];
        if (TryGetPreferredValue(result, row, "DatabaseSchema", out var preferredValue) ||
            TryGetPreferredValue(result, row, "Schema", out preferredValue))
        {
            return preferredValue!;
        }

        for (var i = 0; i < row.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                return row[i]!;
            }
        }

        throw new UserFacingException("Kusto returned an empty database schema payload.");
    }

    private static Dictionary<string, string?> BuildPropertiesFromDatabaseSchema(string schemaJson, string database, string tableName)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var databaseElement = FindDatabaseElement(document.RootElement, database);
            if (!TryGetNamedProperty(databaseElement, "Tables", out var tablesElement) ||
                tablesElement.ValueKind != JsonValueKind.Object ||
                !TryGetNamedProperty(tablesElement, tableName, out var tableElement) ||
                tableElement.ValueKind != JsonValueKind.Object)
            {
                throw new UserFacingException($"Table '{tableName}' was not found.");
            }

            if (!TryGetNamedProperty(tableElement, "OrderedColumns", out var orderedColumnsElement) ||
                orderedColumnsElement.ValueKind != JsonValueKind.Array)
            {
                throw new UserFacingException($"Kusto returned a schema for table '{tableName}' without OrderedColumns.");
            }

            var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TableName"] = GetStringProperty(tableElement, "Name") ?? tableName,
                ["Schema"] = orderedColumnsElement.GetRawText(),
                ["DatabaseName"] = GetStringProperty(databaseElement, "Name") ?? database
            };

            AddOptionalProperty(properties, tableElement, "Folder");
            AddOptionalProperty(properties, tableElement, "DocString");

            return properties;
        }
        catch (UserFacingException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }
    }

    private static string? ExtractDatabaseSchemaVersion(string schemaJson, string database)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var databaseElement = FindDatabaseElement(document.RootElement, database);

            var explicitVersion = GetStringProperty(databaseElement, "Version");
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                return explicitVersion;
            }

            if (TryGetNamedProperty(databaseElement, "MajorVersion", out var majorVersionElement) &&
                TryGetNamedProperty(databaseElement, "MinorVersion", out var minorVersionElement) &&
                TryGetInt32(majorVersionElement, out var majorVersion) &&
                TryGetInt32(minorVersionElement, out var minorVersion))
            {
                return $"v{majorVersion}.{minorVersion}";
            }

            return null;
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected database schema format.", ex);
        }
    }

    private static JsonElement FindDatabaseElement(JsonElement rootElement, string database)
    {
        if (TryGetNamedProperty(rootElement, "Databases", out var databasesElement) &&
            databasesElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetNamedProperty(databasesElement, database, out var databaseElement))
            {
                return databaseElement;
            }

            if (databasesElement.EnumerateObject().FirstOrDefault() is { Name: not null } firstDatabase)
            {
                return firstDatabase.Value;
            }
        }

        if (TryGetNamedProperty(rootElement, "Tables", out _))
        {
            return rootElement;
        }

        throw new UserFacingException($"Kusto did not return a schema for database '{database}'.");
    }

    private static bool TryGetNamedProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return TryGetNamedProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), out value),
            _ => false
        };
    }

    private static void AddOptionalProperty(Dictionary<string, string?> properties, JsonElement element, string propertyName)
    {
        var value = GetStringProperty(element, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[propertyName] = value;
        }
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

    private static bool TryGetPreferredValue(TabularData table, IReadOnlyList<string?> row, string columnName, out string? value)
    {
        value = null;
        if (!table.TryGetColumnIndex(columnName, out var columnIndex) ||
            columnIndex >= row.Count ||
            string.IsNullOrWhiteSpace(row[columnIndex]))
        {
            return false;
        }

        value = row[columnIndex];
        return true;
    }
}
