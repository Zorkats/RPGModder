using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

public class PluginEntry
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("status")]
    public bool Status { get; set; } = true;

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("parameters")]
    public Dictionary<string, string> Parameters { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
