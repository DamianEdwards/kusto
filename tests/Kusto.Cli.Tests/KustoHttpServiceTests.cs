using System.Net;
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
                    [ "ddtelinsights" ]
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
            "https://ddtelinsights.kusto.windows.net",
            null,
            ".show databases | project DatabaseName",
            CancellationToken.None);

        Assert.Equal(["DatabaseName"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("ddtelinsights", result.Rows[0][0]);
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
            "https://ddtelinsights.kusto.windows.net",
            "DDTelInsights",
            "print ValidationInline=1",
            CancellationToken.None);

        Assert.Equal(["ValidationInline"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("1", result.Rows[0][0]);
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(_responseFactory());
        }
    }
}
