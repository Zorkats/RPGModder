using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

public class JsonPatch
{
    [JsonProperty("target")]
    public string Target { get; set; } = "";

    [JsonProperty("merge_data")]
    public JObject MergeData { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
