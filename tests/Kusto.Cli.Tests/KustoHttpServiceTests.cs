using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class KustoHttpServiceTests
{
    [Fact]
    public async Task ExecuteManagementCommandAsync_SelectsPrimaryResultAndDeserializesPascalCasePayload()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "QueryStatus",
                  "TableKind": "QueryCompletionInformation",
                  "Columns": [
                    { "ColumnName": "Status", "DataType": "string" }
                  ],
                  "Rows": [
                    [ "Completed" ]
                  ]
                },
                {
                  "TableName": "Table_0",
                  "TableKind": "PrimaryResult",
                  "Columns": [
                    { "ColumnName": "DatabaseName", "DataType": "string" }
                  ],
                  "Rows": [
                    [ "Samples" ]
                  ]
                }
              ]
            }
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteManagementCommandAsync(
            "https://help.kusto.windows.net",
            null,
            ".show databases | project DatabaseName",
            null,
            CancellationToken.None);

        Assert.Equal(["DatabaseName"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("Samples", result.Rows[0][0]);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("fake-token", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ParsesV2FrameArrayPayload()
    {
        const string responseJson =
            """
            [
              { "FrameType": "DataSetHeader", "IsProgressive": false },
              {
                "FrameType": "DataTable",
                "TableName": "PrimaryResult",
                "TableKind": "PrimaryResult",
                "Columns": [
                  { "ColumnName": "ValidationInline", "ColumnType": "long" }
                ],
                "Rows": [
                  [ 1 ]
                ]
              },
              { "FrameType": "DataSetCompletion", "HasErrors": false }
            ]
            """;

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            "https://help.kusto.windows.net",
            "Samples",
            "print ValidationInline=1",
            includeStatistics: false,
            CancellationToken.None);

        Assert.Equal(["ValidationInline"], result.Table.Columns);
        Assert.Single(result.Table.Rows);
        Assert.Equal("1", result.Table.Rows[0][0]);
        Assert.StartsWith(
            "https://dataexplorer.azure.com/clusters/help.kusto.windows.net/databases/Samples?query=",
            result.WebExplorerUrl);
        Assert.Null(result.Statistics);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithShowStats_ExtractsStatisticsFromStatusDescription()
    {
        var statsPayload = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["ExecutionTime"] = 1.234,
                ["resource_usage"] = new Dictionary<string, object?>
                {
                    ["cpu"] = new Dictionary<string, object?>
                    {
                        ["total cpu"] = "00:00:01.5",
                        ["breakdown"] = new Dictionary<string, object?>
                        {
                            ["query execution"] = "00:00:01.2",
                            ["query planning"] = "00:00:00.3"
                        }
                    },
                    ["memory"] = new Dictionary<string, object?>
                    {
                        ["peak_per_node"] = 47395635
                    },
                    ["cache"] = new Dictionary<string, object?>
                    {
                        ["shards"] = new Dictionary<string, object?>
                        {
                            ["hot"] = new Dictionary<string, object?>
                            {
                                ["hitbytes"] = 126353408,
                                ["missbytes"] = 3355443
                            }
                        }
                    },
                    ["network"] = new Dictionary<string, object?>
                    {
                        ["cross_cluster_total_bytes"] = 5452595,
                        ["inter_cluster_total_bytes"] = 0
                    }
                },
                ["input_dataset_statistics"] = new Dictionary<string, object?>
                {
                    ["extents"] = new Dictionary<string, object?>
                    {
                        ["scanned"] = 42,
                        ["total"] = 1000
                    },
                    ["rows"] = new Dictionary<string, object?>
                    {
                        ["scanned"] = 50000,
                        ["total"] = 1000000
                    }
                },
                ["dataset_statistics"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["table_row_count"] = 150,
                        ["table_size"] = 12800
                    }
                },
                ["cross_cluster_resource_usage"] = new Dictionary<string, object?>
                {
                    ["https://clustername.region.kusto.windows.net/"] = new Dictionary<string, object?>
                    {
                        ["cpu"] = new Dictionary<string, object?>
                        {
                            ["total cpu"] = "00:00:00.8"
                        },
                        ["memory"] = new Dictionary<string, object?>
                        {
                            ["peak_per_node"] = 23173530
                        },
                        ["cache"] = new Dictionary<string, object?>
                        {
                            ["shards"] = new Dictionary<string, object?>
                            {
                                ["hot"] = new Dictionary<string, object?>
                                {
                                    ["hitbytes"] = 52428800,
                                    ["missbytes"] = 1048576
                                }
                            }
                        }
                    }
                }
            });

        var responseJson = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    FrameType = "DataTable",
                    TableName = "PrimaryResult",
                    TableKind = "PrimaryResult",
                    Columns = new object[]
                    {
                        new { ColumnName = "ValidationInline", ColumnType = "long" }
                    },
                    Rows = new object?[][]
                    {
                        [1]
                    }
                },
                new
                {
                    FrameType = "DataTable",
                    TableName = "QueryCompletionInformation",
                    TableKind = "QueryCompletionInformation",
                    Columns = new object[]
                    {
                        new { ColumnName = "SeverityName", ColumnType = "string" },
                        new { ColumnName = "StatusDescription", ColumnType = "string" }
                    },
                    Rows = new object?[][]
                    {
                        ["Stats", statsPayload]
                    }
                }
            });

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            "https://help.kusto.windows.net",
            "Samples",
            "print ValidationInline=1",
            includeStatistics: true,
            CancellationToken.None);

        Assert.NotNull(result.Statistics);
        Assert.Equal(1.234, result.Statistics.ExecutionTimeSec);
        Assert.Equal(45.2, result.Statistics.MemoryPeakPerNodeMb);
        Assert.Equal(5.2, result.Statistics.Network?.CrossClusterMb);
        Assert.Equal(42, result.Statistics.Extents?.Scanned);
        Assert.Equal(150, result.Statistics.Result?.RowCount);
        Assert.Equal("00:00:00.8", result.Statistics.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].CpuTotal);
        Assert.Equal(22.1, result.Statistics.CrossClusterBreakdown?["clustername.region.kusto.windows.net"].MemoryPeakMb);
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithShowStats_ExtractsStatisticsFromPayload()
    {
        var responseJson = JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    FrameType = "DataTable",
                    TableName = "PrimaryResult",
                    TableKind = "PrimaryResult",
                    Columns = new object[]
                    {
                        new { ColumnName = "ValidationInline", ColumnType = "long" }
                    },
                    Rows = new object?[][]
                    {
                        [1]
                    }
                },
                new
                {
                    FrameType = "DataTable",
                    TableName = "QueryCompletionInformation",
                    TableKind = "QueryCompletionInformation",
                    Columns = new object[]
                    {
                        new { ColumnName = "EventTypeName", ColumnType = "string" },
                        new { ColumnName = "Payload", ColumnType = "string" }
                    },
                    Rows = new object?[][]
                    {
                        ["QueryResourceConsumption", "{\"ExecutionTime\":2.5}"]
                    }
                }
            });

        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var result = await service.ExecuteQueryAsync(
            "https://help.kusto.windows.net",
            "Samples",
            "print ValidationInline=1",
            includeStatistics: true,
            CancellationToken.None);

        Assert.NotNull(result.Statistics);
        Assert.Equal(2.5, result.Statistics.ExecutionTimeSec);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OnBadRequest_ReturnsActionableMessageWithoutStatusCode()
    {
        const string responseBody = "Bad request: Syntax error: Unexpected token ';' at position 13";
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteQueryAsync(
                "https://help.kusto.windows.net",
                "Samples",
                "invalid query;",
                includeStatistics: false,
                CancellationToken.None));

        Assert.Contains("Kusto rejected the query or command", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unexpected token ';'", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("400", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bad request:", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_OnBadRequest_HidesServiceMetadata()
    {
        const string responseBody =
            """
            General_BadRequest: Request is invalid and cannot be executed.
            Error details:
            ClientRequestId='unspecified;123', ActivityId='456', Timestamp='2026-01-01T00:00:00.0000000Z'.
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(responseBody)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(() =>
            service.ExecuteManagementCommandAsync(
                "https://help.kusto.windows.net",
                "Samples",
                ".show tables",
                null,
                CancellationToken.None));

        Assert.Equal("Kusto rejected the query or command. Check your syntax and verify the selected cluster and database.", exception.Message);
        Assert.DoesNotContain("ClientRequestId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActivityId", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Timestamp", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteManagementCommandAsync_WithQueryParameters_SerializesPropertiesPayload()
    {
        const string responseJson =
            """
            {
              "Tables": [
                {
                  "TableName": "PrimaryResult",
                  "TableKind": "PrimaryResult",
                  "Columns": [{ "ColumnName": "DatabaseName", "DataType": "string" }],
                  "Rows": [["Samples"]]
                }
              ]
            }
            """;
        var handler = new RecordingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson)
        });
        using var httpClient = new HttpClient(handler);
        var service = new KustoHttpService(httpClient, new StaticTokenProvider("fake-token"), NullLogger<KustoHttpService>.Instance);

        _ = await service.ExecuteManagementCommandAsync(
            "https://help.kusto.windows.net",
            null,
            "declare query_parameters(filterValue:string); .show databases | where DatabaseName contains filterValue",
            new Dictionary<string, string> { ["filterValue"] = "'Sam'" },
            CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        using var requestDocument = JsonDocument.Parse(handler.LastRequestBody!);
        var propertiesElement = requestDocument.RootElement.GetProperty("properties");
        Assert.Equal("'Sam'", propertiesElement.GetProperty("Parameters").GetProperty("filterValue").GetString());
    }

    private sealed class StaticTokenProvider(string token) : ITokenProvider
    {
        public Task<string> GetTokenAsync(string clusterUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory = responseFactory;

        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory();
        }
    }
}
