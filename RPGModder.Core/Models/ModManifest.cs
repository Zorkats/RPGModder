using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// A mod's <c>mod.json</c>. A missing <c>schema_version</c> means version 1 (GR-V1); fields this
/// build does not know are kept and written back unchanged (GR-V5).
/// </summary>
public class ModManifest
{
    /// <summary>The highest manifest version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonProperty("schema_version", NullValueHandling = NullValueHandling.Ignore)]
    public int? SchemaVersion { get; set; }

    [JsonProperty("metadata")]
    public ModMetadata Metadata { get; set; } = new();

    [JsonProperty("file_ops")]
    public List<FileOperation> FileOps { get; set; } = new();

    [JsonProperty("json_patches")]
    public List<JsonPatch> JsonPatches { get; set; } = new();

    [JsonProperty("plugins_registry")]
    public List<PluginEntry> PluginsRegistry { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    /// <summary>The version the file declares, with a missing field read as 1.</summary>
    [JsonIgnore]
    public int EffectiveSchemaVersion => SchemaVersion ?? 1;

    /// <summary>False for a manifest newer than this build, which is listed as unusable and never rewritten (GR-V2).</summary>
    [JsonIgnore]
    public bool IsSupported => EffectiveSchemaVersion <= CurrentSchemaVersion;
}
