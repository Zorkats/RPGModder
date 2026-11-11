using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

public class FileOperation
{
    [JsonProperty("source")]
    public string Source { get; set; } = "";

    [JsonProperty("target")]
    public string Target { get; set; } = "";

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
