# `kusto`

A native command-line tool for Azure Data Explorer (Kusto), focused on quick exploration and query execution from a terminal.

## What this CLI supports

- Manage known clusters (`cluster` command group)
- Manage databases and defaults (`database` command group)
- Browse tables and schemas (`table` command group)
- Run KQL from inline text, files, or stdin (`query`)
- Include Azure Data Explorer Web Explorer deeplinks in query results
- Show optional query execution statistics with `--show-stats`
- Multiple output formats (`human`, `json`, `markdown`/`md`)
- Configurable log verbosity with structured console/file logging

## Authentication

The CLI uses `DefaultAzureCredential`.  
If your current credential chain cannot authenticate to Kusto, sign in with Azure CLI:

```powershell
az login
```

## Quick start

```powershell
# 1) Add a cluster (first cluster becomes default automatically)
kusto cluster add help https://help.kusto.windows.net/

# 2) Set default database for that cluster
kusto database set-default Samples --cluster help

# 3) Run a query
kusto query "StormEvents | take 5"
```

## Configuration

The CLI stores configuration at:

- Default: `%USERPROFILE%\.kusto\config.json`
- Override with environment variable: `KUSTO_CONFIG_PATH`

Example (PowerShell):

```powershell
$env:KUSTO_CONFIG_PATH = "C:\temp\kusto\config.json"
```

## Global options

These options are available on all commands:

| Option | Values | Default | Description |
|---|---|---|---|
| `--format` | `human`, `json`, `markdown`, `md` | `human` | Output format. |
| `--log-level` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None` | not set | Enables console logging at the selected level (logs are always written to file). |
| `-h`, `--help` | n/a | n/a | Show help. |
| `--version` | n/a | n/a | Show version. |

## Command reference

| Command | Purpose | Arguments | Options |
|---|---|---|---|
| `cluster list` | List configured clusters and defaults. | none | global options |
| `cluster show <cluster>` | Show details for one known cluster. | `cluster` (name or URL) | global options |
| `cluster add <name> <url>` | Add a cluster to local config. | `name`, `url` | global options |
| `cluster remove <cluster>` | Remove a known cluster and its default DB mapping. | `cluster` (name or URL) | global options |
| `cluster set-default <cluster>` | Set the default cluster. | `cluster` (name or URL) | global options |
| `database list` | List databases in a cluster. | none | `--cluster`, `--filter`, `--take`, global options |
| `database show <database>` | Show details for one database. | `database` | `--cluster`, global options |
| `database set-default <database>` | Set default database for a cluster. | `database` | `--cluster`, global options |
| `table list` | List tables in a database. | none | `--cluster`, `--database`, `--filter`, `--take`, global options |
| `table show <table>` | Show schema/details for one table. | `table` | `--cluster`, `--database`, global options |
| `query [<query>]` | Run KQL from inline text, file, or stdin. | optional `query` | `--file`, `--cluster`, `--database`, `--show-stats`, global options |

## Command-specific option details

| Option | Commands | Description |
|---|---|---|
| `--cluster <name|url>` | `database *`, `table *`, `query` | Cluster to use. If omitted, default cluster is used. |
| `--database <database>` | `table *`, `query` | Database to use. If omitted, default DB for selected cluster is used. |
| `--filter <value>` | `database list`, `table list` | Name filter. Supports contains/startswith/endswith semantics using anchors (see below). |
| `--take <int>` | `database list`, `table list` | Limits number of rows returned. Must be a positive integer. |
| `--file <path>` | `query` | Read query text from file. Cannot be combined with inline query argument. |
| `--show-stats` | `query` | Include query execution statistics when Kusto returns them. |

### `--filter` semantics

`--filter` is evaluated before request submission and translated to KQL safely:

- `value` -> `contains`
- `^prefix` -> `startswith`
- `suffix$` -> `endswith`
- `^exact$` -> both `startswith` and `endswith`

Invalid values are rejected locally (for example `^$`, empty/whitespace, or misplaced `^`/`$`) and no query is sent.

## Realistic examples

### Cluster commands

```powershell
# Add and inspect a cluster
kusto cluster add help https://help.kusto.windows.net/
kusto cluster show help
kusto cluster list

# Set and remove defaults/clusters
kusto cluster set-default help
kusto cluster remove help
```

### Database commands

```powershell
# List databases from default cluster
kusto database list

# List using explicit cluster and filter/take
kusto database list --cluster help --filter "^Sam" --take 5
kusto database list --cluster help --filter "ples$"

# Show one database
kusto database show Samples --cluster help

# Set default DB for a cluster
kusto database set-default Samples --cluster help
```

### Table commands

```powershell
# List tables using default DB for selected/default cluster
kusto table list --cluster help --database Samples

# Filtered table listing
kusto table list --cluster help --database Samples --filter "^Storm" --take 10

# Show a specific table schema/details
kusto table show StormEvents --cluster help --database Samples
```

### Query command

```powershell
# Inline query
kusto query "StormEvents | summarize Count=count() by State | top 10 by Count desc" --cluster help --database Samples

# Query from file
kusto query --file .\queries\top-states.kql --cluster help --database Samples

# Query from stdin
@"
StormEvents
| where StartTime > ago(7d)
| take 20
"@ | kusto query - --cluster help --database Samples

# Query with execution statistics when available
kusto query "StormEvents | summarize Count=count() by State" --cluster help --database Samples --show-stats
```

## Output formats

```powershell
# Human-friendly terminal rendering
kusto table list --cluster help --database Samples --format human

# JSON for scripts/tools
kusto database list --cluster help --format json

# Markdown for docs/issues
kusto query "StormEvents | take 3" --cluster help --database Samples --format markdown
```

Human and markdown output show a short `Open in Web Explorer` link when available instead of printing the raw `webExplorerUrl`; JSON output still includes `webExplorerUrl`, and `--show-stats` adds `statistics`.

## Logging

- Log file path (default): `%TEMP%\kusto\kusto.log`
- Use `--log-level` to emit console logs in addition to file logs.

```powershell
kusto query "StormEvents | take 1" --cluster help --database Samples --log-level Information
```

## Build and test

```powershell
dotnet build kusto.slnx
dotnet test kusto.slnx
```

## Run from source

```powershell
.\kusto --query "StormEvents | take 5" --cluster help --database Samples
```

## Publish the native executable locally

Windows:

```powershell
dotnet publish .\src\Kusto.Cli\ --os win [--arch <arch>]
```

macOS:

```bash
dotnet publish ./src/Kusto.Cli/ --os osx [--arch <arch>]
```

Linux:

```bash
dotnet publish ./src/Kusto.Cli/ --os linux [--arch <arch>]
```

`<arch>` can be `x64` or `arm64`. If omitted, the current machine's architecture is used.

## License

MIT
