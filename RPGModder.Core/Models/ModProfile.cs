using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// A profile file under <c>.rpgmodder/profiles</c>. Property names keep the casing of the 2.0.0
/// files. Fields this build does not know are kept and written back unchanged (GR-V9).
/// </summary>
public sealed class ModProfile
{
    /// <summary>The highest profile version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The version of the file. A profile created in memory is current; a file without the field is version 1 (GR-V1).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // The list of Folder Names (e.g. "WidescreenMod") that are enabled, in load order
    public List<string> EnabledMods { get; set; } = new();

    // Full load order for all mods (including disabled ones)
    // This preserves the order when mods are toggled on/off
    public List<string> LoadOrder { get; set; } = new();

    /// <summary>The player's answers to collisions, one per collision id.</summary>
    public List<ProfileResolution> Resolutions { get; set; } = new();

    /// <summary>The game rules switch band and its assignments, or null while no rule is in use.</summary>
    public ProfileRules? Rules { get; set; }

    public ProfileOptions Options { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    /// <summary>False for a profile newer than this build, which is listed as unavailable and never rewritten (GR-V2).</summary>
    [JsonIgnore]
    public bool IsSupported => SchemaVersion <= CurrentSchemaVersion;

    /// <summary>A deep copy, so a workflow on another thread never sees the caller's later edits.</summary>
    public ModProfile Clone()
    {
        var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
        return JsonConvert.DeserializeObject<ModProfile>(JsonConvert.SerializeObject(this), settings)!;
    }
}
