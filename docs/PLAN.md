# Kusto CLI Implementation Plan

## Problem statement
Build a standalone `kusto` executable CLI in C# using the latest stable `System.CommandLine`, designed for native AOT publishing, with command groups for cluster/database/table/query operations, robust logging, and user-friendly error handling.

## Current codebase state
- `D:\src\GitHub\DamianEdwards\kusto` is currently empty (no solution/project files detected).
- Implementation will start from a clean repository scaffold.

## Proposed approach
1. **Scaffold solution and projects (AOT-first)**
   - Create a .NET solution with CLI project (for example `src/Kusto.Cli`) and tests project (`tests/Kusto.Cli.Tests`).
   - Target a current stable .NET SDK/runtime that supports native AOT well.
   - Configure publish profiles/scripts for standalone executable distribution (not a .NET global tool).

2. **Set native AOT and trimming constraints up-front**
   - Enable native AOT-related project settings (for example: `PublishAot`, trimming-safe configuration, and self-contained publish configuration as needed by target RID matrix).
   - Prefer APIs and libraries that are known to be trim/AOT friendly.
   - Add a dedicated AOT publish validation step in CI/local verification.

3. **Dependency selection for AOT compatibility**
   - Use latest stable `System.CommandLine`.
   - Use `Azure.Identity` + `Azure.Core` for `DefaultAzureCredential` token acquisition.
   - Use `Microsoft.Extensions.Logging` abstractions and custom logging provider(s).
   - Use `System.Text.Json` with source generation for output and API payloads.
   - **Kusto access strategy:** verify official Kusto .NET client AOT compatibility; if not clearly compatible, use direct Kusto HTTP API endpoints via `HttpClient` as the primary implementation path.

4. **Define core architecture**
   - `CommandFactory`/composition root to build command tree.
   - `IConfigStore` for known clusters/defaults persistence.
   - `IKustoConnectionResolver` for effective cluster/database resolution using defaults and overrides.
   - `ITokenProvider` backed by `DefaultAzureCredential`.
   - `IKustoService` abstraction (implemented by HTTP client path for AOT safety).
   - `IOutputFormatter` abstraction for human/json/markdown output.

5. **Implement configuration model and persistence**
   - Persist known clusters and defaults in `%USERPROFILE%\\.kusto\\config.json`.
   - Store:
     - known clusters `{ name, url }`
     - default cluster reference
     - per-cluster default database
   - Validate malformed/missing config with clear user-facing messages.

6. **Implement logging infrastructure (`ILogger`)**
   - Add file-backed logging provider that writes logs to a temp path (for example `%TEMP%\\kusto\\kusto.log`).
   - Default logging level: `Information`.
   - Add shared `--log-level` option on all commands.
   - Console logging should be disabled by default and only enabled when `--log-level` is explicitly provided; console logs must always go to **stderr**.

7. **Implement command surface with shared options**
   - Commands:
     - `cluster`: `list`, `show`, `add`, `remove`, `set-default`
     - `database`: `list`, `show`, `set-default` with `--cluster [name|url]`
     - `table`: `list`, `show` with `--cluster` and `--database`
     - `query`: execute inline query, query file (`--file`), or stdin via `-`
   - Shared options on all commands:
     - `--format` with `json`, `markdown`, `md`, plus human-friendly default
     - `--log-level` mapped to `LogLevel`

8. **Implement Kusto API operations**
   - Use `HttpClient` + bearer token auth to call Kusto endpoints.
   - Implement operations for:
     - database list/show/default selection support (management commands)
     - table list/show with schema output
     - query execution and tabular result rendering
   - Normalize result models to feed output formatters consistently.

9. **Implement robust error handling and UX**
   - Add centralized command execution error handling.
   - Never expose raw exception messages or stack traces to end users.
   - Map known failures (auth, network, invalid cluster/database/table, bad query, config issues) to concise actionable messages.
   - Log detailed exception context to file for troubleshooting.

10. **Testing and verification**
   - Parser tests for command/options (`--format`, `--log-level`, aliases like `md`).
   - Behavior tests for config/default resolution and command handlers.
   - Logging tests for file logger behavior and stderr-only console logging gating.
   - Error handling tests to ensure friendly messages and no raw stack traces.
   - Query input tests (argument vs `--file` vs stdin `-`).
   - Native AOT publish smoke test(s) and runnable binary verification.

## Notes and considerations
- Because the repo is empty, bootstrapping solution structure is the first milestone.
- Implementation should bias toward readability and maintainable factoring without unnecessary abstraction layers.
- Standalone executable packaging is an explicit requirement; do not design for `dotnet tool install` global tool flow.
- To reduce AOT risk, direct HTTP endpoint integration is preferred unless Kusto SDK compatibility is proven.

## Todo breakdown
1. `bootstrap-solution-aot`: Scaffold solution, CLI app, tests, and baseline publish scripts for standalone executable distribution.
2. `configure-native-aot`: Add native AOT/trimming/self-contained publish settings and RID strategy.
3. `select-aot-safe-dependencies`: Add dependency set (`System.CommandLine`, `Azure.Identity`, logging abstractions, JSON source-gen) and document Kusto SDK compatibility decision.
4. `design-core-abstractions`: Define config, resolver, token provider, Kusto service, and formatter contracts.
5. `implement-config-store`: Implement persistent config loading/saving for clusters/defaults.
6. `implement-logging-infrastructure`: Implement temp-file log provider, default info level, and stderr console logging behavior controlled by `--log-level`.
7. `implement-authentication`: Implement token retrieval via `DefaultAzureCredential`.
8. `implement-kusto-http-service`: Implement direct HTTP API client for Kusto management/query operations (or only use SDK if proven AOT-compatible).
9. `implement-command-model`: Build root command tree with shared `--format` and `--log-level` options.
10. `implement-cluster-commands`: Add `cluster` handlers.
11. `implement-database-commands`: Add `database` handlers with cluster override (name or URL).
12. `implement-table-commands`: Add `table` handlers including schema output in `show`.
13. `implement-query-command`: Add query command with inline/`--file`/stdin input support.
14. `implement-error-handling`: Add centralized friendly error mapping and internal detailed logging.
15. `add-tests`: Add parser/behavior/logging/error/AOT-related tests.
16. `verify-native-aot-publish`: Run build/tests and native AOT publish smoke validation.

## Current progress
- Planning is complete and approved.
- Repository initialized locally and pushed to GitHub (https://github.com/DamianEdwards/kusto).
- MIT license, README, and baseline .gitignore are in place.
- Implementation tasks have not started yet.

## Next steps
1. Start with bootstrap-solution-aot (the current ready todo).
2. Scaffold the .NET solution/projects and standalone publish flow.
3. Configure native AOT settings and dependency baseline.
