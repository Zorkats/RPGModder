using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>Per-profile deploy options.</summary>
public sealed class ProfileOptions
{
    /// <summary>Read every written file back and compare its hash before the deploy commits. On by default.</summary>
    public bool VerifyAfterWrite { get; set; } = true;

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
