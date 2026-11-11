using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>The switch band a profile's game rules use, and which rule owns which switch.</summary>
public sealed class ProfileRules
{
    public RuleBand Band { get; set; } = new();

    public List<RuleAssignment> Assignments { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
