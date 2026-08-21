using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public sealed class TableSchemaProvider(
    IKustoService kustoService,
    OfflineTableDataStore offlineTableDataStore,
    SchemaCacheSettingsResolver settingsResolver,
    ILogger<TableSchemaProvider> logger,
    TimeProvider? timeProvider = null) : ITableSchemaProvider
{
    private readonly IKustoService _kustoService = kustoService;
    private readonly OfflineTableDataStore _offlineTableDataStore = offlineTableDataStore;
    private readonly SchemaCacheSettingsResolver _settingsResolver = settingsResolver;
    private readonly ILogger<TableSchemaProvider> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TableSchemaDetails> GetTableSchemaDetailsAsync(
        KustoConfig config,
        ResolvedCluster cluster,
        string database,
        string tableName,
        bool refreshOfflineData,
        CancellationToken cancellationToken)
    {
        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(cluster.Url);
        var settings = _settingsResolver.Resolve(config, normalizedClusterUrl, database);
        var existingEntry = await _offlineTableDataStore.TryReadEntryAsync(config, normalizedClusterUrl, database, cancellationToken);

        DatabaseSchemaCacheEntry? schemaEntry = existingEntry;
        string schemaJson;

        if (refreshOfflineData)
        {
            schemaEntry = await FetchDatabaseSchemaEntryAsync(cluster, normalizedClusterUrl, database, existingEntry, cancellationToken);
            await _offlineTableDataStore.WriteEntryAsync(config, schemaEntry, cancellationToken);
            schemaJson = schemaEntry.SchemaJson;
        }
        else if (settings.Enabled)
        {
            if (HasSchema(existingEntry))
            {
                if (!IsExpired(existingEntry!, settings.Ttl))
                {
                    schemaJson = existingEntry!.SchemaJson;
                }
                else
                {
                    schemaEntry = await RefreshCacheEntryAsync(existingEntry!, cluster, normalizedClusterUrl, database, cancellationToken);
                    await _offlineTableDataStore.WriteEntryAsync(config, schemaEntry, cancellationToken);
                    schemaJson = schemaEntry.SchemaJson;
                }
            }
            else
            {
                schemaEntry = await FetchDatabaseSchemaEntryAsync(cluster, normalizedClusterUrl, database, existingEntry, cancellationToken);
                await _offlineTableDataStore.WriteEntryAsync(config, schemaEntry, cancellationToken);
                schemaJson = schemaEntry.SchemaJson;
            }
        }
        else
        {
            schemaJson = await FetchDatabaseSchemaJsonAsync(cluster, database, cancellationToken);
        }

        try
        {
            return DatabaseSchemaJson.BuildTableSchemaDetails(schemaJson, database, tableName, GetTableNotes(schemaEntry ?? existingEntry, tableName));
        }
        catch (UserFacingException ex) when (!refreshOfflineData && settings.Enabled && HasSchema(existingEntry))
        {
            _logger.LogDebug(ex, "Refreshing cached schema for {ClusterUrl}/{Database} after a cache lookup miss.", normalizedClusterUrl, database);
            schemaEntry = await FetchDatabaseSchemaEntryAsync(cluster, normalizedClusterUrl, database, existingEntry, cancellationToken);
            await _offlineTableDataStore.WriteEntryAsync(config, schemaEntry, cancellationToken);
            return DatabaseSchemaJson.BuildTableSchemaDetails(schemaEntry.SchemaJson, database, tableName, GetTableNotes(schemaEntry, tableName));
        }
    }

    private async Task<DatabaseSchemaCacheEntry> RefreshCacheEntryAsync(
        DatabaseSchemaCacheEntry cacheEntry,
        ResolvedCluster cluster,
        string clusterUrl,
        string database,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cacheEntry.SchemaVersion))
        {
            var refreshedEntry = await TryFetchDatabaseSchemaIfUpdatedAsync(cluster, clusterUrl, database, cacheEntry, cancellationToken);
            if (refreshedEntry is null)
            {
                cacheEntry.CachedAtUtc = _timeProvider.GetUtcNow();
                return cacheEntry;
            }

            return refreshedEntry;
        }

        return await FetchDatabaseSchemaEntryAsync(cluster, clusterUrl, database, cacheEntry, cancellationToken);
    }

    private async Task<DatabaseSchemaCacheEntry> FetchDatabaseSchemaEntryAsync(
        ResolvedCluster cluster,
        string clusterUrl,
        string database,
        DatabaseSchemaCacheEntry? existingEntry,
        CancellationToken cancellationToken)
    {
        var schemaJson = await FetchDatabaseSchemaJsonAsync(cluster, database, cancellationToken);
        return CreateCacheEntry(clusterUrl, database, schemaJson, existingEntry?.TableNotes);
    }

    private async Task<DatabaseSchemaCacheEntry?> TryFetchDatabaseSchemaIfUpdatedAsync(
        ResolvedCluster cluster,
        string clusterUrl,
        string database,
        DatabaseSchemaCacheEntry existingEntry,
        CancellationToken cancellationToken)
    {
        var result = await _kustoService.ExecuteManagementCommandAsync(
            cluster,
            database,
            BuildShowDatabaseSchemaCommand(database, existingEntry.SchemaVersion),
            null,
            cancellationToken);

        if (result.Rows.Count == 0)
        {
            return null;
        }

        return CreateCacheEntry(
            clusterUrl,
            database,
            DatabaseSchemaJson.ExtractSchemaJson(result),
            existingEntry.TableNotes);
    }

    private async Task<string> FetchDatabaseSchemaJsonAsync(
        ResolvedCluster cluster,
        string database,
        CancellationToken cancellationToken)
    {
        var result = await _kustoService.ExecuteManagementCommandAsync(
            cluster,
            database,
            BuildShowDatabaseSchemaCommand(database, null),
            null,
            cancellationToken);

        return DatabaseSchemaJson.ExtractSchemaJson(result);
    }

    private DatabaseSchemaCacheEntry CreateCacheEntry(
        string clusterUrl,
        string database,
        string schemaJson,
        Dictionary<string, List<string>>? tableNotes)
    {
        return new DatabaseSchemaCacheEntry
        {
            CacheFormatVersion = 1,
            ClusterUrl = clusterUrl,
            DatabaseName = database,
            CachedAtUtc = _timeProvider.GetUtcNow(),
            SchemaVersion = DatabaseSchemaJson.ExtractDatabaseSchemaVersion(schemaJson, database),
            SchemaJson = schemaJson,
            TableNotes = CloneNotes(tableNotes)
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

    private IReadOnlyList<string>? GetTableNotes(DatabaseSchemaCacheEntry? cacheEntry, string tableName)
    {
        if (cacheEntry?.TableNotes is null || !cacheEntry.TableNotes.TryGetValue(tableName, out var notes) || notes.Count == 0)
        {
            return null;
        }

        return notes;
    }

    private bool IsExpired(DatabaseSchemaCacheEntry cacheEntry, TimeSpan ttl)
    {
        return _timeProvider.GetUtcNow() - cacheEntry.CachedAtUtc >= ttl;
    }

    private bool HasSchema(DatabaseSchemaCacheEntry? cacheEntry)
    {
        if (cacheEntry is null || string.IsNullOrWhiteSpace(cacheEntry.SchemaJson))
        {
            return false;
        }

        try
        {
            return DatabaseSchemaJson.GetTableNames(cacheEntry.SchemaJson, cacheEntry.DatabaseName).Count > 0;
        }
        catch (UserFacingException ex)
        {
            _logger.LogDebug(ex, "Ignoring unreadable cached schema for {ClusterUrl}/{Database}.", cacheEntry.ClusterUrl, cacheEntry.DatabaseName);
            return false;
        }
    }

    private static Dictionary<string, List<string>> CloneNotes(Dictionary<string, List<string>>? tableNotes)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (tableNotes is null)
        {
            return clone;
        }

        foreach (var pair in tableNotes)
        {
            clone[pair.Key] = [.. pair.Value];
        }

        return clone;
    }
}
