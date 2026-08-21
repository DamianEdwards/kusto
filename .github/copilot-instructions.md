# Copilot instructions for `kusto`

## Build and test commands

Use these from the repository root:

```powershell
# Build all projects
dotnet build kusto.slnx

# Run the full test suite
dotnet test kusto.slnx

# Run a single test (xUnit filter by fully-qualified name)
dotnet test .\tests\Kusto.Cli.Tests\Kusto.Cli.Tests.csproj --filter "FullyQualifiedName~ParserTests.Parse_DatabaseList_AcceptsFilterAndTake"
```

There is no separate lint command configured in this repository.

## High-level architecture

### 1) CLI composition and command wiring
- `src/Kusto.Cli/Program.cs` is the minimal entry point: it builds the root command via `CommandFactory.CreateRootCommand()` and invokes parsing/execution.
- `src/Kusto.Cli/CommandFactory.cs` defines the entire command surface (`cluster`, `database`, `table`, `query`) and binds each action through `CliRunner.RunAsync(...)`.

### 2) Runtime composition and execution flow
- `src/Kusto.Cli/CliRunner.cs` is the command execution wrapper: it parses common options (`--format`, `--log-level`), constructs runtime dependencies, formats output, and centralizes error handling.
- `CliRunner.CreateRuntime(...)` wires `FileConfigStore`, `KustoConnectionResolver`, `AzureTokenProvider`, `KustoHttpService`, `OfflineTableDataStore`, `TableSchemaProvider`, `TableOfflineDataManager`, the confirmation prompt, and `OutputFormatter` into a `CliRuntime`.
- `src/Kusto.Cli/Contracts.cs` defines the core interfaces (`IConfigStore`, `IKustoService`, `IKustoConnectionResolver`, `ITableSchemaProvider`, `ITableOfflineDataManager`, `IConfirmationPrompt`, `IOutputFormatter`) and the `CliRuntime` container.

### 3) Data/config and connection resolution
- `src/Kusto.Cli/FileConfigStore.cs` persists config to `%USERPROFILE%\.kusto\config.json` (or `KUSTO_CONFIG_PATH` override).
- `src/Kusto.Cli/ClusterUtilities.cs` normalizes cluster URLs and config values; defaults and lookups rely on normalized URLs.
- `src/Kusto.Cli/KustoConnectionResolver.cs` resolves effective cluster/database from explicit options or configured defaults.
- `src/Kusto.Cli/OfflineTableDataStore.cs` persists offline table data per normalized cluster/database in the schema-cache directory; each entry can hold cached schema JSON plus per-table notes.
- `src/Kusto.Cli/TableSchemaProvider.cs` reads/refreshes cached schema data for `table show`, while `src/Kusto.Cli/TableOfflineDataManager.cs` owns notes and offline-data import/export/purge/clear flows.

### 4) Kusto transport layer
- `src/Kusto.Cli/KustoHttpService.cs` calls Kusto REST endpoints:
  - management commands: `/v1/rest/mgmt`
  - queries: `/v2/rest/query`
- Response parsing handles both object payloads (`Tables`) and frame-array payloads (`FrameType == DataTable`), then selects `PrimaryResult` when available.
- Request payloads and output JSON use source-generated serializers (`src/Kusto.Cli/KustoJsonSerializerContext.cs`) for AOT-safe serialization.

### 5) Output and logging pipeline
- `src/Kusto.Cli/OutputFormatter.cs` supports `human`, `json`, and `markdown` output; human output now uses Hex1b-backed rendering plus text-table formatting for static terminal output.
- Query result tables are flagged with `CliOutput.IsQueryResultTable` for query-specific table styling/alignment.
- `src/Kusto.Cli/Logging.cs` configures always-on file logging (`%TEMP%\kusto\kusto.log`) and optional stderr logging when `--log-level` is explicitly set.
- `src/Kusto.Cli/KustoConsoleFormatter.cs` and `src/Kusto.Cli/ConsoleStyling.cs` apply console styling (light-gray logs, red errors, ANSI-aware behavior).

### 6) Chart rendering pipeline
- `src/Kusto.Cli/KustoVisualizationExtractor.cs` parses Kusto `render` annotations from query responses into `QueryVisualization`.
- `src/Kusto.Cli/KustoChartCompatibilityAnalyzer.cs` is the compatibility gate for chart rendering. It maps Kusto render kinds to `QueryChartKind`, validates columns/layouts, and produces either a `HumanChart`, a `MarkdownChart`, or explicit reasons why rendering is not supported.
- `src/Kusto.Cli/Models.cs` defines the chart model surface: `QueryChartKind` (`Column`, `Bar`, `Line`, `Pie`) and `QueryChartLayout` (`Simple`, `Grouped`, `Stacked`, `Stacked100`).
- `src/Kusto.Cli/Hex1bChartRenderer.cs` is the terminal renderer used for human output. It renders `Column`, `Bar`, `Line`, and `Pie`.
- `src/Kusto.Cli/MermaidChartRenderer.cs` is the markdown renderer. It renders Mermaid `xychart` output for cartesian charts and Mermaid `pie` output for pie charts.
- In `src/Kusto.Cli/CommandFactory.cs`, `query --chart` chooses Hex1b for `human` output and Mermaid for `markdown`/`md`; JSON output does not support chart rendering.

## Key repository conventions

1. **User-facing errors must be actionable and implementation-agnostic**
   - Use `UserFacingException` for expected CLI/user errors.
   - Let `ErrorMapper`/`CliRunner` surface concise messages; do not expose stack traces or raw HTTP status-code wording to users.

2. **Authentication is routed per cluster**
   - Token acquisition is routed by `RoutingTokenProvider` based on each `ResolvedCluster`'s authentication mode.
   - Clusters with no `authentication` (or `mode: default`) use `DefaultAzureCredential` (`AzureTokenProvider`); for those, credential/access failures guide the user to run `az login`.
   - Clusters with `mode: wam` use the Windows broker (`WamTokenProvider`), acquire tokens silently, and must never fall back to `DefaultAzureCredential`. WAM failures must guide the user to `kusto cluster login <cluster>` — never `az login`.
   - Query-time WAM auth is strictly silent (no UI). Interactive sign-in happens only through the explicit `cluster login`/`logout` commands (`IClusterAuthenticationService`).

3. **Always normalize cluster URLs before persistence or lookup**
   - Use `ClusterUtilities.NormalizeClusterUrl(...)` and `ClusterUtilities.NormalizeConfig(...)`.
   - Default database mappings are keyed by normalized cluster URL.

4. **List filtering behavior is centralized**
   - `database list` and `table list` filtering/take logic goes through `ListQueryBuilder.Build(...)`.
   - `--filter` supports anchor semantics:
     - `value` => `contains`
     - `^prefix` => `startswith`
     - `suffix$` => `endswith`
     - `^exact$` => startswith + endswith
   - Invalid `--filter`/`--take` must fail locally with `UserFacingException` before sending a request.

5. **Output formatting contract**
   - Command handlers should return `CliOutput` (`Message`, `Properties`, `Table`) and rely on `OutputFormatter`.
   - Prefer returning structured data and let formatter handle rendering differences across output modes.

6. **Offline table data is keyed by normalized cluster URL + database**
   - Reuse the schema-cache directory and normalize cluster URLs before persisting or looking up offline table data.
   - `table show` should render table metadata in `Properties`, column details in `Table`, and surface stored notes in the text output.
   - Table notes are stored per table with sequential IDs derived from list order; do not invent separate GUID-style note identifiers.

7. **Destructive offline-data actions must confirm unless forced**
   - `table notes --clear`, `table --purge-offline-data`, and `table --clear-offline-data` should prompt unless `--force` is supplied.
   - Keep destructive-operation prompts explicit and route behavior through the confirmation prompt abstraction rather than ad-hoc console reads in command handlers.

8. **Chart compatibility rules are centralized**
   - Always route render-kind and layout decisions through `KustoChartCompatibilityAnalyzer`; do not duplicate chart compatibility logic in command handlers or formatters.
   - Current render-kind mapping is:
     - `columnchart` => `QueryChartKind.Column`
     - `barchart` => `QueryChartKind.Bar`
     - `linechart` and `timechart` => `QueryChartKind.Line`
     - `piechart` => `QueryChartKind.Pie`
   - Any other Kusto render kind should surface an explicit "not supported" reason rather than silently falling back.

9. **Human vs markdown chart support differs intentionally**
   - Terminal (`human` + `--chart`) currently supports `columnchart`, `barchart`, `linechart`/`timechart`, and `piechart`.
   - Markdown (`markdown`/`md` + `--chart`) supports those same chart kinds.
   - `piechart` terminal output should render via Hex1b's donut/pie support and include a legend with values/percentages when feasible.
   - Mermaid cartesian output currently requires `Simple` layout and exactly one series; if the chart can't be represented faithfully, preserve the table and emit the markdown reason instead of approximating.

10. **Layout support is chart-kind specific**
   - For line charts, supported human layouts are `default`/`unstacked`, `stacked`, and `stacked100`.
   - For column/bar charts, supported human layouts are `default`/`unstacked`, `grouped`, `stacked`, and `stacked100`.
   - Invalid or unsupported layouts must fail compatibility analysis with a clear reason before rendering.
