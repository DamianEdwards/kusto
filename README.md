# kusto

A native AOT-first C# command-line tool for Azure Data Explorer (Kusto), built on the latest stable `System.CommandLine`.

## Project status

Planning is complete. Implementation is next, starting with solution scaffolding and command surface setup.

## Planned capabilities

- Manage known clusters (`cluster` command group)
- Manage databases and defaults (`database` command group)
- Browse tables and schemas (`table` command group)
- Run KQL queries from argument, file, or stdin (`query` command)
- Output formatting for human-readable, JSON, and Markdown views
- Logging with file output and configurable log levels

## License

MIT
