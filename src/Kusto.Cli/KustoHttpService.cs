using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kusto.Cli;

public sealed class KustoHttpService(HttpClient httpClient, ITokenProvider tokenProvider, ILogger<KustoHttpService> logger) : IKustoService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ITokenProvider _tokenProvider = tokenProvider;
    private readonly ILogger<KustoHttpService> _logger = logger;

    public Task<TabularData> ExecuteManagementCommandAsync(
        string clusterUrl,
        string? database,
        string command,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        return ExecuteManagementCommandCoreAsync(clusterUrl, database, command, queryParameters, cancellationToken);
    }

    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        string clusterUrl,
        string database,
        string query,
        bool includeStatistics,
        CancellationToken cancellationToken)
    {
        var tables = await ExecuteAsync(
            clusterUrl,
            "/v2/rest/query",
            new KustoRequestPayload { Db = database, Csl = query },
            cancellationToken);
        var primaryResult = SelectPrimaryResult(tables);

        return new QueryExecutionResult(
            new TabularData(primaryResult.Columns, primaryResult.Rows),
            KustoWebExplorerUrlBuilder.Build(clusterUrl, database, query),
            includeStatistics ? KustoQueryStatisticsExtractor.Extract(tables) : null);
    }

    private async Task<TabularData> ExecuteManagementCommandCoreAsync(
        string clusterUrl,
        string? database,
        string command,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        var db = string.IsNullOrWhiteSpace(database) ? "NetDefaultDB" : database;
        var payload = new KustoRequestPayload
        {
            Db = db,
            Csl = command
        };

        if (queryParameters is { Count: > 0 })
        {
            payload.Properties = new KustoRequestProperties
            {
                Parameters = queryParameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
        }

        var tables = await ExecuteAsync(clusterUrl, "/v1/rest/mgmt", payload, cancellationToken);
        var primaryResult = SelectPrimaryResult(tables);
        return new TabularData(primaryResult.Columns, primaryResult.Rows);
    }

    private async Task<List<ParsedKustoTable>> ExecuteAsync(
        string clusterUrl,
        string endpointPath,
        KustoRequestPayload payload,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"{ClusterUtilities.NormalizeClusterUrl(clusterUrl)}{endpointPath}");
        var token = await _tokenProvider.GetTokenAsync(clusterUrl, cancellationToken);

        var payloadJson = JsonSerializer.Serialize(payload, KustoJsonSerializerContext.Default.KustoRequestPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Kusto request to {Uri} failed with status code {StatusCode}. Body: {Body}", requestUri, (int)response.StatusCode, body);
            throw new UserFacingException(CreateUserFacingError(response.StatusCode, body));
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var tables = ParseTables(responseBody);
        if (tables.Count == 0)
        {
            throw new UserFacingException("Kusto response did not contain any result tables.");
        }

        return tables;
    }

    private static ParsedKustoTable SelectPrimaryResult(List<ParsedKustoTable> tables)
    {
        return
            tables.FirstOrDefault(t => string.Equals(t.TableKind, "PrimaryResult", StringComparison.OrdinalIgnoreCase)) ??
            tables.FirstOrDefault(t => string.Equals(t.TableName, "PrimaryResult", StringComparison.OrdinalIgnoreCase)) ??
            tables.FirstOrDefault(t => t.Rows.Count > 0) ??
            tables[0];
    }

    private static List<ParsedKustoTable> ParseTables(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var tables = new List<ParsedKustoTable>();

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "Tables", out var tablesElement) && tablesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tableElement in tablesElement.EnumerateArray())
                    {
                        if (tableElement.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        tables.Add(ParseTable(tableElement));
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var frame in root.EnumerateArray())
                {
                    if (frame.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var frameType = GetStringProperty(frame, "FrameType");
                    if (!string.Equals(frameType, "DataTable", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    tables.Add(ParseTable(frame));
                }
            }

            return tables;
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("Kusto returned an unexpected response format.", ex);
        }
    }

    private static ParsedKustoTable ParseTable(JsonElement tableElement)
    {
        var tableName = GetStringProperty(tableElement, "TableName");
        var tableKind = GetStringProperty(tableElement, "TableKind");
        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();

        if (TryGetPropertyIgnoreCase(tableElement, "Columns", out var columnsElement) &&
            columnsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in columnsElement.EnumerateArray())
            {
                if (column.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                columns.Add(GetStringProperty(column, "ColumnName") ??
                    GetStringProperty(column, "Name") ??
                    string.Empty);
            }
        }

        if (TryGetPropertyIgnoreCase(tableElement, "Rows", out var rowsElement) &&
            rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rowsElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var rowValues = row.EnumerateArray().ToArray();
                var values = new string?[columns.Count == 0 ? rowValues.Length : columns.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    if (i >= rowValues.Length)
                    {
                        values[i] = string.Empty;
                        continue;
                    }

                    values[i] = Convert(rowValues[i]);
                }

                rows.Add(values);
            }
        }

        return new ParsedKustoTable(tableName, tableKind, columns, rows);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? Convert(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static string CreateUserFacingError(HttpStatusCode statusCode, string responseBody)
    {
        var detail = ExtractActionableDetail(responseBody);

        return statusCode switch
        {
            HttpStatusCode.BadRequest => string.IsNullOrWhiteSpace(detail)
                ? "Kusto rejected the query or command. Check your syntax and verify the selected cluster and database."
                : $"Kusto rejected the query or command: {detail}",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Kusto rejected this request because your identity does not have access. Run 'az login' and verify access to the cluster and database.",
            _ => string.IsNullOrWhiteSpace(detail)
                ? "Kusto request failed. Verify the cluster, database, and your access."
                : $"Kusto request failed: {detail}"
        };
    }

    private static string? ExtractActionableDetail(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        var lines = responseBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var normalizedLine = line;
            if (normalizedLine.StartsWith("Bad request:", StringComparison.OrdinalIgnoreCase))
            {
                normalizedLine = normalizedLine["Bad request:".Length..].Trim();
            }
            else if (normalizedLine.StartsWith("General_BadRequest:", StringComparison.OrdinalIgnoreCase))
            {
                normalizedLine = normalizedLine["General_BadRequest:".Length..].Trim();
            }

            if (string.IsNullOrWhiteSpace(normalizedLine))
            {
                continue;
            }

            if (normalizedLine.StartsWith("Error details:", StringComparison.OrdinalIgnoreCase) ||
                normalizedLine.Contains("ClientRequestId=", StringComparison.OrdinalIgnoreCase) ||
                normalizedLine.Contains("ActivityId=", StringComparison.OrdinalIgnoreCase) ||
                normalizedLine.Contains("Timestamp=", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedLine, "Request is invalid and cannot be executed.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return normalizedLine;
        }

        return null;
    }
}
