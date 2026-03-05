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
[JsonSerializable(typeof(KustoConfig))]
[JsonSerializable(typeof(KustoRequestPayload))]
[JsonSerializable(typeof(KustoResponsePayload))]
[JsonSerializable(typeof(KustoResponseTable))]
[JsonSerializable(typeof(KustoResponseColumn))]
[JsonSerializable(typeof(List<KustoResponseTable>))]
[JsonSerializable(typeof(List<KustoResponseColumn>))]
[JsonSerializable(typeof(List<List<JsonElement>>))]
[JsonSerializable(typeof(TabularData))]
internal sealed partial class KustoJsonSerializerContext : JsonSerializerContext
{
}
