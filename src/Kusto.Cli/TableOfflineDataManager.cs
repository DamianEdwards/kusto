using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public sealed class TableOfflineDataManager(
    IKustoService kustoService,
    OfflineTableDataStore offlineTableDataStore,
    ILogger<TableOfflineDataManager> logger) : ITableOfflineDataManager
{
    private readonly IKustoService _kustoService = kustoService;
    private readonly OfflineTableDataStore _offlineTableDataStore = offlineTableDataStore;
    private readonly ILogger<TableOfflineDataManager> _logger = logger;

    public async Task<CliOutput> ShowTableNotesAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        int? noteId,
        CancellationToken cancellationToken)
    {
        var entry = await _offlineTableDataStore.TryReadEntryAsync(config, clusterUrl, database, cancellationToken);
        var notes = GetNotes(entry, tableName);

        if (noteId is int id)
        {
            EnsurePositiveId(id, "--id");
            if (id > notes.Count)
            {
                throw new UserFacingException($"Note {id} was not found for table '{tableName}'.");
            }

            return new CliOutput
            {
                Table = BuildNotesTable([(id, notes[id - 1])])
            };
        }

        if (notes.Count == 0)
        {
            return new CliOutput
            {
                Message = $"No notes found for table '{tableName}'."
            };
        }

        return new CliOutput
        {
            Table = BuildNotesTable(notes.Select((note, index) => (index + 1, note)))
        };
    }

    public async Task<CliOutput> AddTableNoteAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        string note,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new UserFacingException("The --add value cannot be empty.");
        }

        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var entry = await _offlineTableDataStore.TryReadEntryAsync(config, normalizedClusterUrl, database, cancellationToken) ??
            CreateEmptyEntry(normalizedClusterUrl, database);

        if (!entry.TableNotes.TryGetValue(tableName, out var notes))
        {
            notes = [];
            entry.TableNotes[tableName] = notes;
        }

        notes.Add(note.Trim());
        await _offlineTableDataStore.WriteEntryAsync(config, entry, cancellationToken);

        return new CliOutput
        {
            Message = $"Added note {notes.Count.ToString(CultureInfo.InvariantCulture)} for table '{tableName}'."
        };
    }

    public async Task<CliOutput> DeleteTableNoteAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        string tableName,
        int noteId,
        CancellationToken cancellationToken)
    {
        EnsurePositiveId(noteId, "--delete");

        var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl);
        var entry = await _offlineTableDataStore.TryReadEntryAsync(config, normalizedClusterUrl, database, cancellationToken);
        var notes = GetNotes(entry, tableName);
        if (noteId > notes.Count)
        {
            throw new UserFacingException($"Note {noteId} was not found for table '{tableName}'.");
        }

        notes.RemoveAt(noteId - 1);
        if (notes.Count == 0 && entry is not null)
        {
            entry.TableNotes.Remove(tableName);
        }

        await PersistOrDeleteAsync(config, entry, cancellationToken);
        return new CliOutput
        {
            Message = $"Deleted note {noteId.ToString(CultureInfo.InvariantCulture)} from table '{tableName}'."
        };
    }

    public async Task<CliOutput> ClearTableNotesAsync(
        KustoConfig config,
        string? clusterUrl,
        string? database,
        string? tableName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl!);
            var entry = await _offlineTableDataStore.TryReadEntryAsync(config, normalizedClusterUrl, database!, cancellationToken);
            var notes = GetNotes(entry, tableName);
            if (notes.Count == 0)
            {
                return new CliOutput
                {
                    Message = $"No notes found for table '{tableName}'."
                };
            }

            entry!.TableNotes.Remove(tableName);
            await PersistOrDeleteAsync(config, entry, cancellationToken);
            return new CliOutput
            {
                Message = $"Cleared {notes.Count.ToString(CultureInfo.InvariantCulture)} notes for table '{tableName}'."
            };
        }

        var entries = await _offlineTableDataStore.ReadAllEntriesAsync(config, cancellationToken);
        var removedNotes = 0;
        foreach (var entry in entries)
        {
            removedNotes += entry.TableNotes.Values.Sum(notes => notes.Count);
            entry.TableNotes.Clear();
            await PersistOrDeleteAsync(config, entry, cancellationToken);
        }

        return new CliOutput
        {
            Message = removedNotes == 0
                ? "No table notes were found."
                : $"Cleared {removedNotes.ToString(CultureInfo.InvariantCulture)} table notes."
        };
    }

    public async Task<CliOutput> ExportOfflineDataAsync(
        KustoConfig config,
        string filePath,
        CancellationToken cancellationToken)
    {
        var exportPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(exportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entries = await _offlineTableDataStore.ReadAllEntriesAsync(config, cancellationToken);
        var payload = new OfflineTableDataExport
        {
            Entries =
            [
                .. entries
                    .OrderBy(entry => entry.ClusterUrl, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.DatabaseName, StringComparer.OrdinalIgnoreCase)
            ]
        };

        await using var stream = File.Create(exportPath);
        await JsonSerializer.SerializeAsync(
            stream,
            payload,
            KustoJsonSerializerContext.Default.OfflineTableDataExport,
            cancellationToken);

        return new CliOutput
        {
            Message = $"Exported {payload.Entries.Count.ToString(CultureInfo.InvariantCulture)} offline data entries to '{exportPath}'."
        };
    }

    public async Task<CliOutput> ImportOfflineDataAsync(
        KustoConfig config,
        string filePath,
        CancellationToken cancellationToken)
    {
        var importPath = Path.GetFullPath(filePath);
        if (!File.Exists(importPath))
        {
            throw new UserFacingException($"Offline data file '{importPath}' was not found.");
        }

        await using var stream = File.OpenRead(importPath);
        var payload = await JsonSerializer.DeserializeAsync(
            stream,
            KustoJsonSerializerContext.Default.OfflineTableDataExport,
            cancellationToken);

        if (payload is null)
        {
            throw new UserFacingException("The offline data import file is empty.");
        }

        if (payload.FormatVersion != 1)
        {
            throw new UserFacingException($"Offline data format version '{payload.FormatVersion}' is not supported.");
        }

        var entriesByKey = new Dictionary<string, DatabaseSchemaCacheEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in payload.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ClusterUrl) || string.IsNullOrWhiteSpace(entry.DatabaseName))
            {
                throw new UserFacingException("The offline data import file contains an entry without clusterUrl or databaseName.");
            }

            var normalizedEntry = new DatabaseSchemaCacheEntry
            {
                CacheFormatVersion = 1,
                ClusterUrl = ClusterUtilities.NormalizeClusterUrl(entry.ClusterUrl),
                DatabaseName = entry.DatabaseName.Trim(),
                CachedAtUtc = entry.CachedAtUtc == default ? DateTimeOffset.UtcNow : entry.CachedAtUtc,
                SchemaVersion = entry.SchemaVersion,
                SchemaJson = entry.SchemaJson ?? string.Empty,
                TableNotes = NormalizeNotes(entry.TableNotes)
            };

            entriesByKey[$"{normalizedEntry.ClusterUrl}|{normalizedEntry.DatabaseName}"] = normalizedEntry;
        }

        foreach (var entry in entriesByKey.Values)
        {
            await _offlineTableDataStore.WriteEntryAsync(config, entry, cancellationToken);
        }

        return new CliOutput
        {
            Message = $"Imported {entriesByKey.Count.ToString(CultureInfo.InvariantCulture)} offline data entries from '{importPath}'."
        };
    }

    public async Task<CliOutput> PurgeOfflineDataAsync(
        KustoConfig config,
        CancellationToken cancellationToken)
    {
        var entries = await _offlineTableDataStore.ReadAllEntriesAsync(config, cancellationToken);
        if (entries.Count == 0)
        {
            return new CliOutput
            {
                Message = "No offline table data was found."
            };
        }

        var changedDatabases = 0;
        var removedTables = 0;
        var removedNotes = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trackedTables = GetTrackedTableNames(entry);
            if (trackedTables.Count == 0)
            {
                await _offlineTableDataStore.DeleteEntryAsync(config, entry.ClusterUrl, entry.DatabaseName, cancellationToken);
                changedDatabases++;
                continue;
            }

            var liveTables = await GetLiveTablesAsync(config, entry.ClusterUrl, entry.DatabaseName, cancellationToken);
            var missingTables = trackedTables
                .Where(table => !liveTables.Contains(table))
                .ToList();

            if (missingTables.Count == 0)
            {
                continue;
            }

            changedDatabases++;
            removedTables += missingTables.Count;

            foreach (var missingTable in missingTables)
            {
                if (entry.TableNotes.TryGetValue(missingTable, out var notes))
                {
                    removedNotes += notes.Count;
                    entry.TableNotes.Remove(missingTable);
                }

                entry.SchemaJson = DatabaseSchemaJson.RemoveTable(entry.SchemaJson, entry.DatabaseName, missingTable);
            }

            await PersistOrDeleteAsync(config, entry, cancellationToken);
        }

        return new CliOutput
        {
            Message = removedTables == 0
                ? "No stale offline table data was found."
                : $"Purged offline data for {removedTables.ToString(CultureInfo.InvariantCulture)} tables across {changedDatabases.ToString(CultureInfo.InvariantCulture)} databases and removed {removedNotes.ToString(CultureInfo.InvariantCulture)} notes."
        };
    }

    public async Task<CliOutput> ClearOfflineDataAsync(
        KustoConfig config,
        string? clusterUrl,
        string? database,
        string? tableName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tableName))
        {
            var normalizedClusterUrl = ClusterUtilities.NormalizeClusterUrl(clusterUrl!);
            var entry = await _offlineTableDataStore.TryReadEntryAsync(config, normalizedClusterUrl, database!, cancellationToken);
            if (entry is null)
            {
                return new CliOutput
                {
                    Message = $"No offline data found for table '{tableName}'."
                };
            }

            var removedNoteCount = entry.TableNotes.TryGetValue(tableName, out var notes) ? notes.Count : 0;
            var hadSchema = DatabaseSchemaJson.GetTableNames(entry.SchemaJson, entry.DatabaseName)
                .Any(name => string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase));

            entry.TableNotes.Remove(tableName);
            entry.SchemaJson = DatabaseSchemaJson.RemoveTable(entry.SchemaJson, entry.DatabaseName, tableName);

            if (!hadSchema && removedNoteCount == 0)
            {
                return new CliOutput
                {
                    Message = $"No offline data found for table '{tableName}'."
                };
            }

            await PersistOrDeleteAsync(config, entry, cancellationToken);
            return new CliOutput
            {
                Message = $"Cleared offline data for table '{tableName}'."
            };
        }

        var entries = await _offlineTableDataStore.ReadAllEntriesAsync(config, cancellationToken);
        var removedDatabases = entries.Count;
        var removedTables = entries.Sum(entry => GetTrackedTableNames(entry).Count);
        var removedNotes = entries.Sum(entry => entry.TableNotes.Values.Sum(notes => notes.Count));

        foreach (var entry in entries)
        {
            await _offlineTableDataStore.DeleteEntryAsync(config, entry.ClusterUrl, entry.DatabaseName, cancellationToken);
        }

        return new CliOutput
        {
            Message = removedDatabases == 0
                ? "No offline table data was found."
                : $"Cleared offline data for {removedTables.ToString(CultureInfo.InvariantCulture)} tables across {removedDatabases.ToString(CultureInfo.InvariantCulture)} databases and removed {removedNotes.ToString(CultureInfo.InvariantCulture)} notes."
        };
    }

    private async Task<HashSet<string>> GetLiveTablesAsync(
        KustoConfig config,
        string clusterUrl,
        string database,
        CancellationToken cancellationToken)
    {
        var result = await _kustoService.ExecuteManagementCommandAsync(
            ClusterUtilities.ResolveClusterForUrl(config, clusterUrl),
            database,
            ".show tables | project TableName",
            null,
            cancellationToken);

        var liveTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameColumnIndex = result.TryGetColumnIndex("TableName", out var index) ? index : 0;
        foreach (var row in result.Rows)
        {
            if (nameColumnIndex >= 0 &&
                nameColumnIndex < row.Count &&
                !string.IsNullOrWhiteSpace(row[nameColumnIndex]))
            {
                liveTables.Add(row[nameColumnIndex]!);
            }
        }

        return liveTables;
    }

    private static DatabaseSchemaCacheEntry CreateEmptyEntry(string clusterUrl, string database)
    {
        return new DatabaseSchemaCacheEntry
        {
            CacheFormatVersion = 1,
            ClusterUrl = clusterUrl,
            DatabaseName = database,
            CachedAtUtc = DateTimeOffset.UtcNow,
            TableNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task PersistOrDeleteAsync(
        KustoConfig config,
        DatabaseSchemaCacheEntry? entry,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }

        if (!HasAnyOfflineData(entry))
        {
            await _offlineTableDataStore.DeleteEntryAsync(config, entry.ClusterUrl, entry.DatabaseName, cancellationToken);
            return;
        }

        await _offlineTableDataStore.WriteEntryAsync(config, entry, cancellationToken);
    }

    private static List<string> GetNotes(DatabaseSchemaCacheEntry? entry, string tableName)
    {
        if (entry?.TableNotes is null || !entry.TableNotes.TryGetValue(tableName, out var notes))
        {
            return [];
        }

        return notes;
    }

    private static TabularData BuildNotesTable(IEnumerable<(int Id, string Note)> notes)
    {
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var note in notes)
        {
            rows.Add([note.Id.ToString(CultureInfo.InvariantCulture), note.Note]);
        }

        return new TabularData(
            ["Id", "Note"],
            rows);
    }

    private static HashSet<string> GetTrackedTableNames(DatabaseSchemaCacheEntry entry)
    {
        var trackedTables = new HashSet<string>(
            DatabaseSchemaJson.GetTableNames(entry.SchemaJson, entry.DatabaseName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var tableName in entry.TableNotes.Keys)
        {
            trackedTables.Add(tableName);
        }

        return trackedTables;
    }

    private static bool HasAnyOfflineData(DatabaseSchemaCacheEntry entry)
    {
        return DatabaseSchemaJson.GetTableNames(entry.SchemaJson, entry.DatabaseName).Count > 0 ||
               entry.TableNotes.Values.Any(notes => notes.Count > 0);
    }

    private static Dictionary<string, List<string>> NormalizeNotes(Dictionary<string, List<string>>? tableNotes)
    {
        var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (tableNotes is null)
        {
            return normalized;
        }

        foreach (var pair in tableNotes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var notes = pair.Value?
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Select(note => note.Trim())
                .ToList();

            if (notes is { Count: > 0 })
            {
                normalized[pair.Key.Trim()] = notes;
            }
        }

        return normalized;
    }

    private void EnsurePositiveId(int value, string optionName)
    {
        if (value <= 0)
        {
            throw new UserFacingException($"The {optionName} value must be a positive integer.");
        }
    }
}
