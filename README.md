# `kusto`

A native command-line tool for Azure Data Explorer (Kusto).

## Project status

Initial implementation is in place and actively evolving.

## Implemented command surface

- Manage known clusters (`cluster` command group)
- Manage databases and defaults (`database` command group)
- Browse tables and schemas (`table` command group)
- Run KQL queries from argument, file, or stdin (`query` command)
- Output formatting for human-readable, JSON, and Markdown views
- Logging with file output and configurable log levels

## Build and test

```powershell
dotnet build kusto.slnx
dotnet test kusto.slnx
```

## Native AOT publish (standalone executable)

```powershell
.\scripts\publish-aot.ps1 -RuntimeIdentifier win-x64
```

## License

MIT
