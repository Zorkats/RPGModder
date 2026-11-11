using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>The audit counts for one installed mod, named by its library folder (TX-21).</summary>
public sealed class BaselineAuditMod
{
    [JsonProperty("mod")]
    public string Mod { get; set; } = string.Empty;

    [JsonProperty("same_content")]
    public int SameContent { get; set; }

    [JsonProperty("same_content_and_time")]
    public int SameContentAndTime { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
