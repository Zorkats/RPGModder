using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// The baseline audit (TX-21): how many candidate vanilla files equal a file of an installed mod,
/// per level. <c>same_content_and_time</c> counts the files that also share the mod file's
/// last-write time to the second; <c>same_content</c> counts the others.
/// </summary>
public sealed class BaselineAudit
{
    [JsonProperty("audited_utc")]
    public DateTimeOffset AuditedUtc { get; set; }

    [JsonProperty("same_content")]
    public int SameContent { get; set; }

    [JsonProperty("same_content_and_time")]
    public int SameContentAndTime { get; set; }

    [JsonProperty("mods")]
    public List<BaselineAuditMod> Mods { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    [JsonIgnore]
    public bool HasMatches => SameContent + SameContentAndTime > 0;

    /// <summary>"name (2 same content, 1 same content and time)" per mod, for the warning's subject.</summary>
    public string Describe()
    {
        return string.Join(", ", Mods.Select(mod =>
            $"{mod.Mod} ({mod.SameContent} same content, {mod.SameContentAndTime} same content and time)"));
    }
}
