using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

public class ModMetadata
{
    [JsonProperty("name")]
    public string Name { get; set; } = "Unknown Mod";

    [JsonProperty("author")]
    public string Author { get; set; } = "Unknown";

    [JsonProperty("version")]
    public string Version { get; set; } = "1.0";

    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("nexus_id")]
    public int? NexusId { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    // Cached evaluation flag for O(1) UI performance
    [JsonProperty("is_fhmm_mod")]
    public bool IsFhmmMod { get; set; } = false;

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
