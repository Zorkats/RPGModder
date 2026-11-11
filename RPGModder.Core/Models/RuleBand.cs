using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>A run of switch ids reserved for game rules: <see cref="Start"/> and the next <see cref="Count"/> - 1.</summary>
public sealed class RuleBand
{
    public int Start { get; set; }

    public int Count { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
