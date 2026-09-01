# `kusto`

A native command-line tool for Azure Data Explorer (Kusto), focused on quick exploration and query execution from a terminal.

## Install now on Windows

In a PowerShell terminal:

```PowerShell
irm https://kusto.damianedwards.dev/install.ps1 | iex
```

## Install on macOS or Linux

In a terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh | bash
```

## What this CLI supports

- Manage known clusters (`cluster` command group)
- Manage databases and defaults (`database` command group)
- Browse tables, schema details, and offline table data (`table` command group)
- Run KQL from inline text, files, or stdin (`query`)
- Show copy/paste-ready examples and aliases (`examples`)
- Include Azure Data Explorer Web Explorer deeplinks in query results
- Surface Kusto `render` metadata in query results when visualization annotations are returned
- Render compatible `render` results as terminal charts with `--chart`, as Mermaid in markdown output, or as PNG images via `--output-chart`
- Show optional query execution statistics with `--show-stats`
- Basic public, US Government, and China cloud support for token audience selection and Web Explorer links
- Multiple output formats (`human`, `json`, `markdown`/`md`, plus query-only `csv`)
- Optional offline table data with TTL-based schema revalidation, per-table notes, and import/export support
- Configurable log verbosity with structured console/file logging
- Signed and attested installers plus checksum/provenance-verifying self-update
- Generated completion scripts for bash, zsh, fish, and PowerShell
- GitHub Actions workflows for PR validation, versioned native release assets, and release promotion

## Authentication

The CLI uses `DefaultAzureCredential`.
If your current credential chain cannot authenticate to Kusto, sign in with Azure CLI:

```powershell
az login
```

For sovereign clouds, set Azure CLI to the matching cloud before signing in (for example `az cloud set --name AzureUSGovernment` or `az cloud set --name AzureChinaCloud`). The CLI currently auto-selects Kusto token audiences and Web Explorer bases for public, US Government, and China cluster URLs.

## Quick start

```powershell
# 1) Add a cluster (first cluster becomes default automatically)
kusto cluster add help https://help.kusto.windows.net/

# Or add and make it the default in one step
kusto cluster add help https://help.kusto.windows.net/ --use

# 2) Set default database for that cluster
kusto database set-default Samples --cluster https://help.kusto.windows.net

# 3) Run a query
kusto query "StormEvents | take 5"

# Render a compatible chart directly in the terminal
kusto query --chart "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart"

# Save the chart as a PNG image
kusto query --output-chart ./top-states.png "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart"

# Emit Mermaid markdown for a compatible render query
kusto query --format markdown --chart "StormEvents | summarize Count=count() by State | top 5 by Count desc | render piechart"

# Redirect query results directly to CSV
kusto query "StormEvents | summarize EventCount = count() by State | top 10 by EventCount desc" --format csv > top-states.csv

# Need copy/paste examples?
kusto examples
```

## Configuration

The CLI stores configuration at:

- Default: `%USERPROFILE%\.kusto\config.json`
- Override with environment variable: `KUSTO_CONFIG_PATH`

Example (PowerShell):

```powershell
$env:KUSTO_CONFIG_PATH = "C:\temp\kusto\config.json"
```

## Chart rendering

`query --chart` is output-format aware:

- `human`: renders compatible chart types directly in the terminal after the tabular results
- `markdown`: emits Mermaid chart syntax for compatible chart kinds after the markdown table
- `json` / `csv`: rejected, because terminal/markdown chart rendering doesn't apply to JSON or CSV output

Supported render kinds:

| Kusto `render` kind | `human --chart` | `markdown --chart` | `--output-chart` (PNG) | Notes |
|---|---|---|---|---|
| `columnchart` | yes | yes | yes | Human output renders a terminal column chart; markdown emits Mermaid `xychart`; PNG draws via ScottPlot. |
| `barchart` | yes | yes | yes | Human output renders a terminal bar chart; markdown emits Mermaid `xychart horizontal`; PNG draws via ScottPlot. |
| `linechart` | yes | yes | yes | Human output renders a terminal line chart; markdown emits Mermaid `xychart`; PNG draws via ScottPlot. |
| `timechart` | yes | yes | yes | Alias of `linechart`. |
| `piechart` | yes | yes | yes | Human output renders a terminal pie chart with a legend; markdown emits Mermaid `pie`; PNG draws a pie with a value/percentage legend. |

Layout support:

- `linechart` and `timechart` support `default`/`unstacked`, `stacked`, and `stacked100` for terminal and PNG rendering
- `columnchart` and `barchart` support `default`/`unstacked`, `grouped`, `stacked`, and `stacked100` for terminal and PNG rendering
- Mermaid cartesian output currently requires the simple/default layout and exactly one series

### Image output (PNG)

`--output-chart <path.png>` writes the rendered chart to a PNG file using [ScottPlot](https://scottplot.net/) (MIT, headless SkiaSharp backend). It is orthogonal to `--chart`: the file is always written when supplied and works alongside any `--format`, including `json` and `csv`. For display formats (`human`, `markdown`, `json`) the raw tabular data is suppressed and only a chart-written confirmation (plus any stats/visualization metadata) is emitted. For `--format csv` the CSV stream is preserved on stdout so `... --format csv --output-chart x.png > data.csv` writes both the PNG (as a side effect) and the CSV data; the chart-written confirmation goes to stderr in that case. Use it when you want a chart that's readable by image viewers, embeddable in docs, or consumable by multimodal LLMs.

Third-party licenses for the rendering stack are summarized in [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md).

```powershell
# Save a PNG alongside human terminal output
kusto query --chart --output-chart ./top-states.png "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart"

# Save a PNG while emitting CSV to stdout
kusto query --format csv --output-chart ./top-states.png "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart" > top-states.csv

# Custom dimensions
kusto query --output-chart ./pie.png --output-chart-width 1600 --output-chart-height 900 "StormEvents | summarize Count=count() by State | top 5 by Count desc | render piechart"
```

Image-output options:

| Option | Default | Notes |
|---|---|---|
| `--output-chart <path>` | _(off)_ | Path must end in `.png`. Parent directories are created if missing. |
| `--output-chart-width <pixels>` | `1200` | Range 200..8192. Requires `--output-chart`. |
| `--output-chart-height <pixels>` | `675` | Range 200..8192. Requires `--output-chart`. |

Behavior notes:

- The image renderer consumes the same compatibility analysis as the terminal renderer, so any chart kind/layout that renders in the terminal will render to PNG.
- The PNG palette is a neutral, high-contrast set (white background, dark axes/text, saturated, well-separated series colors) chosen for legibility under OCR and multimodal LLM ingestion rather than to match the Hex1b terminal palette pixel-for-pixel.
- If the query has no `render` annotation or the kind is unsupported, `--output-chart` fails with the same reason `--chart` would have surfaced.

Example terminal renderings captured as plain text:

- These examples use the exact Unicode block characters produced by the terminal renderer, shown in fenced code blocks so they can live in the README without screenshots.
- Exact spacing can vary a little by font and viewport width.
- Pie charts are supported in the terminal too, but they rely more heavily on terminal color, so the text-only README examples below focus on column, bar, and line charts.

Column chart example:

```text
                                             Top states
         4,701                    3,166                    2,014                    1,580
████████████████████████
████████████████████████
████████████████████████
████████████████████████
████████████████████████
████████████████████████
████████████████████████ ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁
████████████████████████ ████████████████████████
████████████████████████ ████████████████████████
████████████████████████ ████████████████████████
████████████████████████ ████████████████████████
████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
████████████████████████ ████████████████████████ ████████████████████████ ████████████████████████
         TEXAS                    KANSAS                   NEVADA                    UTAH
```

Bar chart example:

```text
                                           Top databases
             ███████████████████████████████████████████████████████████████████████████████
             ███████████████████████████████████████████████████████████████████████████████
Samples      ███████████████████████████████████████████████████████████████████████████████ 942
             ███████████████████████████████████████████████████████████████████████████████
             ███████████████████████████████████████████████████████████████████████████████

             ████████████
             ████████████
StormEvents  ████████████                                                                    143
             ████████████
             ████████████

             ████▉
             ████▉
NetDefaultDB ████▉                                                                           58
             ████▉
             ████▉

             █▊
             █▊
Weather      █▊                                                                              21
             █▊
             █▊
```

Line chart example:

```text
                                           Request volume
                                                           ⣀⠤⠒⠉⠒⠢⠤⣀⡀
                                                       ⣀⠤⠒⠉        ⠈⠉⠒⠢⠤⣀
                                                   ⣀⠤⠒⠉                  ⠉⠑⠒⠤⢄⣀
                                               ⣀⠤⠒⠉                            ⠉⠑⠢⣀
                                           ⢀⠤⠒⠉                                    ⠑⠤⡀
                                          ⡠⠃                                         ⠈⠒⢄
  400                                    ⡔⠁                                             ⠉⠢⣀
                                       ⢀⠜                                                  ⠑⠤⡀
                                      ⢠⠊                                                     ⠈⠒⢄
                                     ⡰⠁                                                         ⠉⠢⣀
                                    ⡜                                                              ⠑
                                  ⢀⠎
                                 ⡠⠃
  200                           ⡔⠁
                              ⢀⠜
                             ⢠⠊
                            ⡰⠁
      ⠒⠢⠤⢄⣀⡀               ⡜
           ⠈⠉⠑⠒⠢⠤⢄⣀⡀     ⢀⠎
                   ⠈⠉⠑⠒⠢⠤⠃

    0
      00:00           04:00              08:00             12:00              16:00            20:00
```

If a query returns visualization metadata but `--chart` is omitted, the CLI will hint when the result is compatible with terminal chart rendering. If a render kind or layout can't be mapped faithfully to Hex1b or Mermaid, the CLI will keep the table output and show an explanatory message instead.

## Offline table data

`table show` uses on-disk offline table data by default for repeated schema discovery. The CLI caches database schema snapshots, revalidates expired entries with `.show database ['<db>'] schema if_later_than "<version>" as json`, includes table and column docstrings in `table show`, and lets you attach per-table notes that are echoed back in `table show`.

For configuration, cache locations, disable/override behavior, and offline-data management examples, see [docs/schema-cache.md](docs/schema-cache.md).

## Global options

These options are available on all commands:

| Option | Values | Default | Description |
|---|---|---|---|
| `--format` | `human`, `json`, `markdown`, `md`, `csv` | `human` | Output format. `csv` is currently supported only for `query`. |
| `--log-level` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None` | not set | Enables console logging at the selected level (logs are always written to file). |
| `-h`, `--help` | n/a | n/a | Show help. |
| `--version` | n/a | n/a | Show version. |

## Command reference

| Command | Purpose | Arguments | Options |
|---|---|---|---|
| `examples` | Show usage examples, aliases, and quick-start commands. | none | global options |
| `update` | Check for and install a verified CLI update. | none | `--check`, `--pre-release`, `--stable-only`, `--dry-run`, `--skip-provenance-checks` |
| `completions script [<shell>]` | Generate completion for bash, zsh, fish, or PowerShell. | optional shell | `--command-name` |
| `config` | Show configuration or set the prerelease update preference. | none | `--set include_prerelease_updates=true\|false`, global options |
| `cluster list` | List configured clusters and defaults. | none | global options |
| `cluster show <cluster>` | Show details for one known cluster. | `cluster` (name or URL) | global options |
| `cluster add <name> <url>` | Add a cluster to local config. | `name`, `url` | `--use`, global options |
| `cluster remove <cluster>` | Remove a known cluster and its default DB mapping. | `cluster` (name or URL) | global options |
| `cluster set-default <cluster>` | Set the default cluster. | `cluster` (name or URL) | global options |
| `database list` | List databases in a cluster. | none | `--cluster`, `--filter`, `--take`, global options |
| `database show <database>` | Show details for one database. | `database` | `--cluster`, global options |
| `database set-default <database>` | Set default database for a cluster. | `database` | `--cluster`, global options |
| `table [<table>]` | Manage offline table data at the root command level. | optional `table` | `--export-offline-data`, `--import-offline-data`, `--purge-offline-data`, `--clear-offline-data`, `--cluster`, `--database`, `--force`, global options |
| `table list` | List tables in a database. | none | `--cluster`, `--database`, `--filter`, `--take`, global options |
| `table show <table>` | Show table details, column schema, docstrings, and stored notes. | `table` | `--cluster`, `--database`, `--refresh-offline-data`, global options |
| `table notes [<table>]` | List, add, delete, or clear table notes. | optional `table` | `--cluster`, `--database`, `--add`, `--id`, `--delete`, `--clear`, `--force`, global options |
| `query [<query>]` | Run KQL from inline text, file, or stdin. | optional `query` | `--file`, `--cluster`, `--database`, `--chart`, `--output-chart`, `--output-chart-width`, `--output-chart-height`, `--show-stats`, global options |

## Command-specific option details

| Option | Commands | Description |
|---|---|---|
| `--cluster <name\|url>` | `database *`, `table *`, `query` | Cluster to use. If omitted, default cluster is used. |
| `--database <database>` | `table *`, `query` | Database to use. Alias: `--db`. If omitted, default DB for selected cluster is used. |
| `--filter <value>` | `database list`, `table list` | Name filter. Supports contains/startswith/endswith semantics using anchors (see below). |
| `--take <int>` | `database list`, `table list` | Limits number of rows returned. Alias: `--limit`. Must be a positive integer. |
| `--refresh-offline-data` | `table show` | Force a live schema refresh and update the local offline table data. Alias: `-r`. |
| `--add <note>` | `table notes <table>` | Add a note for the specified table. Alias: `-a`. |
| `--id <int>` | `table notes <table>` | Show a specific note by its sequential ID. |
| `--delete <int>` | `table notes <table>` | Delete a specific note by its sequential ID. Alias: `-d`. |
| `--clear` | `table notes`, `table [<table>]` | Clear table notes or clear offline table data, depending on the command context. Alias: `-c`. |
| `--export-offline-data <path>` | `table` | Export all offline table data to JSON. |
| `--import-offline-data <path>` | `table` | Import offline table data from JSON. |
| `--purge-offline-data` | `table` | Remove offline data for tables that no longer exist. Alias: `-p`. |
| `--clear-offline-data` | `table [<table>]` | Clear offline data for one table or for all tables. Alias: `-c`. |
| `--force` | destructive `table` / `table notes` actions | Skip the confirmation prompt when clearing or purging offline data. Alias: `-f`. |
| `--use` | `cluster add` | Also set the added cluster as the active/default cluster. |
| `--file <path>` | `query` | Read query text from file. Append `:<start>-<end>` to read an inclusive 1-based line range. Alias: `-f`. Cannot be combined with inline query argument. |
| `--chart` | `query` | Render compatible query results as a chart for `human` or `markdown` output. Not supported with `json` or `csv`. |
| `--output-chart <path>` | `query` | Write the rendered chart as a PNG to the given path. Works with any `--format`. For `human`/`markdown`/`json` the raw tabular data is suppressed and a chart-written confirmation is emitted on stdout. For `csv` the CSV stream stays on stdout and the chart-written confirmation goes to stderr. Path must end in `.png`. Parent directories are created automatically. |
| `--output-chart-width <pixels>` | `query` | PNG width in pixels for `--output-chart`. Default: `1200`. Range: 200–8192. Requires `--output-chart`. |
| `--output-chart-height <pixels>` | `query` | PNG height in pixels for `--output-chart`. Default: `675`. Range: 200–8192. Requires `--output-chart`. |
| `--show-stats` | `query` | Include query execution statistics when Kusto returns them. Not supported with `csv`. |

## Optional aliases

Canonical command names are used in the examples above. These aliases are still available when you want shorter forms:

| Canonical | Aliases |
|---|---|
| `examples` | `example`, `aliases` |
| `cluster` | `clusters` |
| `database` | `databases`, `db` |
| `table` | `tables` |
| `query` | `run`, `exec` |
| `list` | `ls` |
| `show` | `get` (`table show` also supports `schema`) |
| `remove` | `rm`, `delete` |
| `set-default` | `use` |
| `--database` | `--db` |
| `--take` | `--limit` |
| `--refresh-offline-data` | `-r` |
| `--add` | `-a` |
| `--delete` | `-d` |
| `--clear`, `--clear-offline-data` | `-c` |
| `--purge-offline-data` | `-p` |
| `--file` | `-f` |
| `--force` | `-f` |

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
kusto database list --cluster https://help.kusto.windows.net --filter "^Sam" --take 5
kusto database list --cluster https://help.kusto.windows.net --filter "ples$"

# Show one database
kusto database show Samples --cluster https://help.kusto.windows.net

# Set default DB for a cluster
kusto database set-default Samples --cluster https://help.kusto.windows.net
```

### Table commands

```powershell
# List tables using default DB for selected/default cluster
kusto table list --cluster https://help.kusto.windows.net --database Samples

# Filtered table listing
kusto table list --cluster https://help.kusto.windows.net --database Samples --filter "^Storm" --take 10

# Show a specific table schema/details
kusto table show StormEvents --cluster https://help.kusto.windows.net --database Samples

# Force a live refresh of the cached table details
kusto table show StormEvents --cluster https://help.kusto.windows.net --database Samples --refresh-offline-data

# Add and inspect table notes
kusto table notes StormEvents --cluster https://help.kusto.windows.net --database Samples --add "Use this table for weather samples."
kusto table notes StormEvents --cluster https://help.kusto.windows.net --database Samples

# Delete a specific note or clear notes
kusto table notes StormEvents --cluster https://help.kusto.windows.net --database Samples --delete 1
kusto table notes --clear --force

# Export/import or clear offline data
kusto table --export-offline-data .\offline-table-data.json
kusto table --import-offline-data .\offline-table-data.json
kusto table StormEvents --cluster https://help.kusto.windows.net --database Samples --clear-offline-data --force
kusto table --purge-offline-data --force
```

### Query command

```powershell
# Inline query
kusto query "StormEvents | summarize Count=count() by State | top 10 by Count desc" --cluster https://help.kusto.windows.net --database Samples

# Query from file
kusto query --file .\queries\top-states.kql --cluster https://help.kusto.windows.net --database Samples

# Query a specific line range from a file containing multiple statements
kusto query --file .\queries\top-states.kql:12-15 --cluster https://help.kusto.windows.net --database Samples

# Query from stdin
@"
StormEvents
| where StartTime > ago(7d)
| take 20
"@ | kusto query - --cluster https://help.kusto.windows.net --database Samples

# Query with execution statistics when available
kusto query "StormEvents | summarize Count=count() by State" --cluster https://help.kusto.windows.net --database Samples --show-stats

# Render a terminal bar chart
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render barchart" --cluster https://help.kusto.windows.net --database Samples --chart

# Render a terminal time series chart (`timechart` is an alias of `linechart`)
kusto query "StormEvents | summarize Count=count() by bin(StartTime, 1d) | render timechart" --cluster https://help.kusto.windows.net --database Samples --chart

# Emit Mermaid pie chart output in markdown
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render piechart" --cluster https://help.kusto.windows.net --database Samples --format markdown --chart

# Save a column chart as a PNG (default 1200×675)
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart" --cluster https://help.kusto.windows.net --database Samples --output-chart ./top-states.png

# Save a PNG and render in the terminal at the same time
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart" --cluster https://help.kusto.windows.net --database Samples --chart --output-chart ./top-states.png

# Save a PNG at a custom size
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render piechart" --cluster https://help.kusto.windows.net --database Samples --output-chart ./pie.png --output-chart-width 1600 --output-chart-height 900

# Save a PNG while still streaming results as CSV
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart" --cluster https://help.kusto.windows.net --database Samples --format csv --output-chart ./top-states.png > top-states.csv

# Save a PNG while capturing results as JSON (chartOutputPath appears in the envelope)
kusto query "StormEvents | summarize Count=count() by State | top 5 by Count desc | render columnchart" --cluster https://help.kusto.windows.net --database Samples --format json --output-chart ./top-states.png
```

## Output formats

```powershell
# Human-friendly terminal rendering
kusto table list --cluster https://help.kusto.windows.net --database Samples --format human

# JSON for scripts/tools
kusto database list --cluster https://help.kusto.windows.net --format json

# Markdown for docs/issues
kusto query "StormEvents | take 3" --cluster https://help.kusto.windows.net --database Samples --format markdown

# CSV for redirecting query results
kusto query "StormEvents | summarize EventCount = count() by State | top 10 by EventCount desc" --cluster https://help.kusto.windows.net --database Samples --format csv > top-states.csv
```

Human and markdown output show a short `Open in Web Explorer` link when available instead of printing the raw `webExplorerUrl`; JSON output still includes `webExplorerUrl`, and `--show-stats` adds `statistics`. CSV output is query-only and writes just the tabular result data to stdout, so `--chart` and `--show-stats` are rejected with `--format csv`.

## Logging

- Log file path (default): `%TEMP%\kusto\kusto.log`
- Use `--log-level` to emit console logs in addition to file logs.

```powershell
kusto query "StormEvents | take 1" --cluster https://help.kusto.windows.net --database Samples --log-level Information
```

## Installer scripts

The repository publishes signed/attested installer snapshots from the protected `install-scripts` branch. The signed Windows script is also available at `https://kusto.damianedwards.dev/install.ps1`.

Example usage:

```powershell
# Stable (default)
irm https://kusto.damianedwards.dev/install.ps1 | iex

# Include prereleases
& ([scriptblock]::Create((irm 'https://kusto.damianedwards.dev/install.ps1'))) -Quality PreRelease

# Development build (unsigned assets): prompts for confirmation unless -Force is supplied
& ([scriptblock]::Create((irm 'https://kusto.damianedwards.dev/install.ps1'))) -Quality Dev -Force

# Install to a custom location without modifying PATH
& ([scriptblock]::Create((irm 'https://kusto.damianedwards.dev/install.ps1'))) -TargetPath 'C:\tools\kusto\bin' -UpdatePath:$false
```

```bash
# Stable (default)
curl -fsSL https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh | bash

# Include official prereleases
curl -fsSL https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh |
  bash -s -- --quality PreRelease

# Install to a custom location without modifying a shell profile
curl -fsSL https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh |
  bash -s -- --target-path "$HOME/bin" --no-update-path
```

Installer behavior:

- Script paths in this repo: `scripts/install/install-kusto-cli.ps1` and `scripts/install/install-kusto-cli.sh`
- Supports `-Quality Dev|PreRelease|Stable` (default: `Stable`)
- Supports a target path (default: `%USERPROFILE%\.kusto\bin` or `~/.kusto/bin`)
- Supports `-UpdatePath` (default: `true`)
- Prints concise progress messages by default during download, verification, and install
- Supports `-Verbose` for opt-in download and provenance diagnostics
- Selects the highest matching semantic version instead of relying on GitHub API order
- Treats `Stable` as strictly stable; prereleases require explicit opt-in
- Always verifies archive SHA256 and release metadata, including development builds
- Requires Authenticode trust for `kusto.exe` and every native executable sidecar in official Windows releases
- Requires tag-bound GitHub artifact attestations for official macOS/Linux archives
- Installs the complete payload transactionally and updates PATH plus shell completion setup

## Self-update and completions

```powershell
kusto update --check
kusto update
kusto update --pre-release
kusto update --stable-only
kusto update --dry-run

kusto config --set include_prerelease_updates=true
kusto completions script pwsh
```

Stable installations consider stable updates by default. Official prerelease installations can advance to newer official prereleases or RTM, and development builds can advance to any newer channel. Set `KUSTO_DISABLE_SELF_UPDATES=1` to disable update checks. `KUSTO_UPDATE_REPOSITORY` overrides the GitHub repository; `KUSTO_UPDATE_SOURCE` points to a local release bundle for testing. A local Unix source requires explicit `--skip-provenance-checks`, but checksum and metadata verification still run.

Updates validate the complete extracted payload before installation. Windows replacement runs in a detached helper after the active process exits; Unix replacement runs as a transactional file swap. Both retain a backup until the new binary passes the packaged `_diag chart-self-test` startup check, then remove it. Failed swaps restore the prior payload.

### Manually verify Windows provenance checks

The installer's Windows provenance logic has two supporting scripts:

- `scripts/Verify-WindowsBinaryIssuer.ps1` reuses the installer's trust helpers and verifies signature validity, certificate-chain/timestamp validity, and the installer's configured immediate-issuer and parent-issuer thumbprints.
- `scripts/Test-InstallerProvenance.ps1` stages positive and negative scenarios so you can make the trust checks fail on demand and inspect them with `-Verbose`.

Use a signed `kusto.exe` from a release when you want to validate the installer's actual expected subject and issuer-thumbprint configuration, including parent-intermediate fallback:

```powershell
pwsh .\scripts\Verify-WindowsBinaryIssuer.ps1 -BinaryPath .\artifacts\signed\kusto.exe -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario InstallerDefaults -BinaryPath .\artifacts\signed\kusto.exe -Verbose
```

To create an unsigned local Windows bundle that exercises the checksum, metadata, and unsigned-binary failure paths:

```powershell
Remove-Item .\artifacts\local-release, .\artifacts\local-bundle -Recurse -Force -ErrorAction SilentlyContinue
pwsh .\scripts\Publish-NativeAsset.ps1 -RuntimeIdentifier win-x64 -Version 0.1.0-local -ArtifactsDirectory .\artifacts\local-release
Get-ChildItem .\artifacts\local-release
dotnet .\scripts\merge-release-bundle.cs -- --input-directory .\artifacts\local-release --output-directory .\artifacts\local-bundle --release-version 0.1.0-local
Get-ChildItem .\artifacts\local-bundle
Expand-Archive -Path .\artifacts\local-bundle\kusto-win-x64.zip -DestinationPath .\artifacts\local-bundle\extract -Force
```

After `Publish-NativeAsset`, `.\artifacts\local-release` should contain `kusto-win-x64.zip`, `kusto-win-x64.zip.sha256`, and `kusto-win-x64.json`.

After `merge-release-bundle.cs`, `.\artifacts\local-bundle` should contain `kusto-win-x64.zip`, `checksums.txt`, and `release-metadata.json`. An `extract` directory on its own is just a previous expansion target; it does not mean the bundle zip was created.

Then run the staged failure scenarios:

```powershell
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario UnsignedBinary -BinaryPath .\artifacts\local-bundle\extract\kusto.exe -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario ChecksumMismatch -ArchivePath .\artifacts\local-bundle\kusto-win-x64.zip -ChecksumsPath .\artifacts\local-bundle\checksums.txt -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario MetadataMismatch -ArchivePath .\artifacts\local-bundle\kusto-win-x64.zip -ChecksumsPath .\artifacts\local-bundle\checksums.txt -ReleaseMetadataPath .\artifacts\local-bundle\release-metadata.json -Verbose
```

For generic signature-path exercises, you can use any signed Windows executable. For example, `pwsh.exe` is convenient for validating the positive path and forced subject/thumbprint/signature failures:

```powershell
$pwshPath = (Get-Command pwsh).Source
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario GoodBinary -BinaryPath $pwshPath -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario TamperedBinary -BinaryPath $pwshPath -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario WrongSubject -BinaryPath $pwshPath -Verbose
pwsh .\scripts\Test-InstallerProvenance.ps1 -Scenario WrongIssuer -BinaryPath $pwshPath -Verbose
```

Expected outcomes by scenario:

- `InstallerDefaults` succeeds only when the binary matches the installer's configured signer subject and either a configured immediate issuer thumbprint or, when needed, a configured parent intermediate issuer thumbprint. Root certificates are never used for fallback.
- `GoodBinary` succeeds for a valid signed Windows executable when the expected values are taken from that binary.
- `UnsignedBinary` fails with an Authenticode signature validation error.
- `TamperedBinary` fails with an Authenticode signature validation error after a single-byte mutation invalidates the signature.
- `WrongSubject` fails with a signer-subject mismatch.
- `WrongIssuer` fails when neither the immediate issuer nor the parent intermediate issuer matches the configured allow-lists.
- `ChecksumMismatch` fails with a `SHA256 mismatch` error.
- `MetadataMismatch` fails because `release-metadata.json` no longer matches `checksums.txt`.

The harder timestamp or certificate-chain failure cases still need a lab-signed binary or another controlled fixture, but the verbose output from these scripts shows the exact signer, immediate issuer, parent issuer fallback candidate, timestamp, chain elements, thumbprints, and whether parent-intermediate fallback was attempted.

## Build and test

```powershell
dotnet build kusto.slnx
dotnet test kusto.slnx
```

## CI and release workflow

The release system is split into narrowly scoped workflows:

- `pr.yml` validates scripts, restore/build/test behavior, NativeAOT, and packaged runtime behavior.
- `ci.yml` runs on main pushes or manual dispatch, calculates versions, publishes six development and six promotable archives, creates a versioned development prerelease, and advances `release-state`.
- `bump-version.yml` moves the release state between `pre`, `rc`, and `rtm`.
- `publish-release.yml` validates a successful `main` CI run, creates its annotated version tag, and dispatches promotion.
- `release.yml` promotes the exact prebuilt bundle without rebuilding, requires production approval, signs every Windows executable payload, attests final archives, publishes generated release notes, and advances release state.
- `install-scripts.yml` signs and snapshots both installers to the protected `install-scripts` branch.
- `attest-install-scripts.yml` attests the immutable installer snapshot and publishes its non-latest release.
- `releases-cleanup.yml` retains a configurable number of development and installer snapshots.

Mutable version state lives in `version-state.json` on the workflow-managed `release-state` branch. Release-state writers share one concurrency group.

See [Release, signing, provenance, and self-update](docs/release-and-provenance.md)
for the repository-specific architecture, trust model, setup, recovery, and
verification procedures.

### Typical maintainer flow

1. Open a pull request and let `pr.yml` validate restore/build/test behavior.
2. Merge to `main`, which lets `ci.yml` calculate versions, publish native assets, create a versioned development prerelease, and update `release-state`.
3. When you want to move between `pre`, `rc`, or `rtm`, run `bump-version.yml`.
4. Run `publish-release.yml` for the successful CI run to tag and promote its already-built official bundle.
5. Approve the `production` deployment. The release workflow signs, attests, and publishes the exact tagged bundle.
6. Run `install-scripts.yml` when installer source changes, then approve its signed immutable snapshot.

## Native release asset layout

CI publishes unsigned native assets for:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Release assets are intentionally shaped for stable download URLs and easy platform-specific downloads:

- Windows: zip archives such as `kusto-win-x64.zip`
- Linux/macOS: tarballs such as `kusto-linux-x64.tar.gz`
- Archive contents: `kusto[.exe]`, required SkiaSharp/HarfBuzzSharp/libsodium native sidecars, license/notices, and `payload-manifest.json`
- Bundles always include `checksums.txt` and `release-metadata.json`
- `release.yml` signs every Windows executable payload, regenerates hashes/metadata, and attests all final archives before publishing

If you want to generate the same release-shaped outputs locally, use the helper scripts instead of calling `dotnet publish` directly:

```powershell
pwsh .\scripts\Publish-NativeAsset.ps1 -RuntimeIdentifier win-x64 -Version 0.1.0 -ArtifactsDirectory .\artifacts\local-release
dotnet .\scripts\merge-release-bundle.cs -- --input-directory .\artifacts\local-release --output-directory .\artifacts\local-bundle --release-version 0.1.0
```

## NativeAOT prerequisites

NativeAOT publishing needs platform-specific native toolchains in addition to the .NET SDK:

- Windows: Visual Studio C++ tools / Desktop development with C++ (ARM64 publishing also needs ARM64 C++ tools)
- Linux ARM64 cross-publish on Ubuntu: `clang`, `llvm`, `binutils-aarch64-linux-gnu`, `gcc-aarch64-linux-gnu`, and `zlib1g-dev:arm64`
- macOS: Xcode command line tools

The GitHub workflows install or configure the required toolchains for CI. When publishing locally, make sure the NativeAOT prerequisites for your target runtime are available first.

## Run from source

```powershell
.\kusto.cmd query "StormEvents | take 5" --cluster https://help.kusto.windows.net --database Samples
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
