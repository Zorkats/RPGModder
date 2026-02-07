using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

public class ModManifest
{
    [JsonProperty("metadata")]
    public ModMetadata Metadata { get; set; } = new();

    [JsonProperty("file_ops")]
    public List<FileOperation> FileOps { get; set; } = new();

    [JsonProperty("json_patches")]
    public List<JsonPatch> JsonPatches { get; set; } = new();

    [JsonProperty("plugins_registry")]
    public List<PluginEntry> PluginsRegistry { get; set; } = new();
}

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
}

public class FileOperation
{
    [JsonProperty("source")]
    public string Source { get; set; } = "";

    [JsonProperty("target")]
    public string Target { get; set; } = "";
}

public class JsonPatch
{
    [JsonProperty("target")]
    public string Target { get; set; } = "";

    [JsonProperty("merge_data")]
    public JObject MergeData { get; set; } = new();
}

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
}