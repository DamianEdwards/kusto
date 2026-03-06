# `kusto`

A native command-line tool for Azure Data Explorer (Kusto), focused on quick exploration and query execution from a terminal.

## What this CLI supports

- Manage known clusters (`cluster` command group)
- Manage databases and defaults (`database` command group)
- Browse tables and schemas (`table` command group)
- Run KQL from inline text, files, or stdin (`query`)
- Show copy/paste-ready examples and aliases (`examples`)
- Include Azure Data Explorer Web Explorer deeplinks in query results
- Show optional query execution statistics with `--show-stats`
- Basic public, US Government, and China cloud support for token audience selection and Web Explorer links
- Multiple output formats (`human`, `json`, `markdown`/`md`)
- Configurable log verbosity with structured console/file logging
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
kusto database set-default Samples --cluster help

# 3) Run a query
kusto query "StormEvents | take 5"

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
| `examples` | Show usage examples, aliases, and quick-start commands. | none | global options |
| `cluster list` | List configured clusters and defaults. | none | global options |
| `cluster show <cluster>` | Show details for one known cluster. | `cluster` (name or URL) | global options |
| `cluster add <name> <url>` | Add a cluster to local config. | `name`, `url` | `--use`, global options |
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
| `--database <database>` | `table *`, `query` | Database to use. Alias: `--db`. If omitted, default DB for selected cluster is used. |
| `--filter <value>` | `database list`, `table list` | Name filter. Supports contains/startswith/endswith semantics using anchors (see below). |
| `--take <int>` | `database list`, `table list` | Limits number of rows returned. Alias: `--limit`. Must be a positive integer. |
| `--use` | `cluster add` | Also set the added cluster as the active/default cluster. |
| `--file <path>` | `query` | Read query text from file. Alias: `-f`. Cannot be combined with inline query argument. |
| `--show-stats` | `query` | Include query execution statistics when Kusto returns them. |

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
| `--file` | `-f` |

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

## CI and release workflow

The repository includes four GitHub Actions workflows:

- `pr.yml` - restore/build/test validation for pull requests across Ubuntu, Windows, and macOS
- `ci.yml` - version calculation, build/test validation, native asset publishing, and dev draft release updates on `main`
- `release.yml` - manual promotion of the prebuilt RC bundle into a GitHub release without rebuilding
- `bump-version.yml` - manual semantic-version / phase transitions for `pre`, `rc`, and `rtm`

Version state is stored in the body of the draft `dev` release so CI can calculate the next development and release-candidate versions without committing version files into the repo.

### Typical maintainer flow

1. Open a pull request and let `pr.yml` validate restore/build/test behavior.
2. Merge to `main`, which lets `ci.yml` calculate versions, publish native assets, and refresh the draft `dev` release.
3. When you want to move between `pre`, `rc`, or `rtm`, run `bump-version.yml`.
4. When the RC bundle is the one you want to ship, run `release.yml` to promote those already-built artifacts into the GitHub release.
5. The next push to `main` refreshes the draft `dev` release for ongoing development.

## Native release asset layout

CI publishes native assets for:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Release assets are intentionally shaped for stable download URLs and easy platform-specific downloads:

- All platforms: archives such as `kusto-win-x64.zip` and `kusto-linux-x64.zip`
- Archive contents: `kusto.exe` on Windows, `kusto` on Linux/macOS, plus `LICENSE`
- Bundles always include `checksums.txt` and `release-metadata.json`

If you want to generate the same release-shaped outputs locally, use the helper scripts instead of calling `dotnet publish` directly:

```powershell
pwsh .\scripts\Publish-NativeAsset.ps1 -RuntimeIdentifier win-x64 -Version 0.1.0 -ArtifactsDirectory .\artifacts\local-release
pwsh .\scripts\Merge-ReleaseBundle.ps1 -InputDirectory .\artifacts\local-release -OutputDirectory .\artifacts\local-bundle -Version 0.1.0
```

## NativeAOT prerequisites

NativeAOT publishing needs platform-specific native toolchains in addition to the .NET SDK:

- Windows: Visual Studio C++ tools / Desktop development with C++ (ARM64 publishing also needs ARM64 C++ tools)
- Linux ARM64 cross-publish on Ubuntu: `clang`, `llvm`, `binutils-aarch64-linux-gnu`, `gcc-aarch64-linux-gnu`, and `zlib1g-dev:arm64`
- macOS: Xcode command line tools

The GitHub workflows install or configure the required toolchains for CI. When publishing locally, make sure the NativeAOT prerequisites for your target runtime are available first.

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
