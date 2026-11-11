using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>How the live folder differs from the store's files where no deploy or mod explains it (TX-24).</summary>
public sealed class BaselineCounts
{
    [JsonProperty("changed")]
    public int Changed { get; set; }

    [JsonProperty("missing")]
    public int Missing { get; set; }

    [JsonProperty("not_listed")]
    public int NotListed { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    [JsonIgnore]
    public bool Any => Changed + Missing + NotListed > 0;

    public override string ToString()
    {
        return $"{Changed} changed, {Missing} missing, {NotListed} not listed by the store";
    }
}
