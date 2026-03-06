namespace Kusto.Cli;

internal static class KustoCommandText
{
    public static string EscapeSingleQuotedLiteral(string input)
    {
        return input.Replace("'", "''", StringComparison.Ordinal);
    }

    public static string EscapeDoubleQuotedLiteral(string input)
    {
        return input.Replace("\"", "\"\"", StringComparison.Ordinal);
    }
}
