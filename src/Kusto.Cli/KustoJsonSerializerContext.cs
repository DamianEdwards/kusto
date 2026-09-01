using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kusto.Cli;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(CliOutput))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(KnownCluster))]
[JsonSerializable(typeof(ClusterAuthentication))]
[JsonSerializable(typeof(KustoConfig))]
[JsonSerializable(typeof(SchemaCacheConfig))]
[JsonSerializable(typeof(SchemaCacheOverride))]
[JsonSerializable(typeof(OfflineTableDataExport))]
[JsonSerializable(typeof(KustoAuthMetadataEnvelope))]
[JsonSerializable(typeof(KustoAzureAdMetadata))]
[JsonSerializable(typeof(KustoRequestPayload))]
[JsonSerializable(typeof(KustoRequestProperties))]
[JsonSerializable(typeof(KustoResponsePayload))]
[JsonSerializable(typeof(KustoResponseTable))]
[JsonSerializable(typeof(KustoResponseColumn))]
[JsonSerializable(typeof(List<KustoResponseTable>))]
[JsonSerializable(typeof(List<KustoResponseColumn>))]
[JsonSerializable(typeof(List<List<JsonElement>>))]
[JsonSerializable(typeof(QueryStatistics))]
[JsonSerializable(typeof(QueryVisualization))]
[JsonSerializable(typeof(QueryCpuStatistics))]
[JsonSerializable(typeof(QueryCacheStatistics))]
[JsonSerializable(typeof(QueryNetworkStatistics))]
[JsonSerializable(typeof(QueryCountStatistics))]
[JsonSerializable(typeof(QueryResultStatistics))]
[JsonSerializable(typeof(QueryCrossClusterStatistics))]
[JsonSerializable(typeof(Dictionary<string, QueryCrossClusterStatistics>))]
[JsonSerializable(typeof(DatabaseSchemaCacheEntry))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(TabularData))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(PayloadManifest))]
internal sealed partial class KustoJsonSerializerContext : JsonSerializerContext
{
}
