using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>One file of the vanilla backup: path relative to the content root with forward slashes, size, SHA-256.</summary>
public sealed class VanillaFileEntry
{
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
