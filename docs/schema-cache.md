# Schema cache

`table show` uses an on-disk schema cache by default. The CLI caches database schema snapshots and revalidates expired entries with `.show database ['<db>'] schema if_later_than "<version>" as json` before downloading a full replacement.

## Defaults

- Default: enabled
- Default cache paths:
  - Windows: `%LOCALAPPDATA%\kusto\schema-cache`
  - Linux: `$XDG_CACHE_HOME\kusto\schema-cache` or `~/.cache/kusto/schema-cache`
  - macOS: `~/Library/Caches/kusto/schema-cache`

## Environment overrides

- `KUSTO_SCHEMA_CACHE_ENABLED`
- `KUSTO_SCHEMA_CACHE_PATH`
- `KUSTO_SCHEMA_CACHE_TTL_SECONDS`

Set `KUSTO_SCHEMA_CACHE_ENABLED=false` or `"enabled": false` in `config.json` if you want to turn the cache off.

## Example: PowerShell

```powershell
# Override the default TTL for this shell
$env:KUSTO_SCHEMA_CACHE_TTL_SECONDS = "3600"

# First call populates the cache
kusto table show StormEvents --cluster help --database Samples

# Later calls reuse the cached database schema until TTL expiry,
# then revalidate with if_later_than before downloading a replacement
kusto table show StormEvents --cluster help --database Samples
```

## Example: config.json

```json
{
  "clusters": [
    {
      "name": "help",
      "url": "https://help.kusto.windows.net"
    }
  ],
  "defaultClusterUrl": "https://help.kusto.windows.net",
  "defaultDatabases": {
    "https://help.kusto.windows.net": "Samples"
  },
  "schemaCache": {
    "ttlSeconds": 86400,
    "overrides": [
      {
        "clusterUrl": "https://help.kusto.windows.net",
        "database": "Samples",
        "ttlSeconds": 3600
      }
    ]
  }
}
```
