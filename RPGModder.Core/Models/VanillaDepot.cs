using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>A depot and its manifest id at the time the backup was proven (GR-V10).</summary>
public sealed class VanillaDepot
{
    [JsonProperty("depot_id")]
    public string DepotId { get; set; } = string.Empty;

    [JsonProperty("manifest_id")]
    public string ManifestId { get; set; } = string.Empty;

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
