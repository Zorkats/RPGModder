using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// The switch one rule owns. The optional <c>Scope</c> field arrives in slice 2c; until then it is
/// kept in <see cref="ExtensionData"/> like any other unknown field.
/// </summary>
public sealed class RuleAssignment
{
    public string RuleId { get; set; } = "";

    public int SwitchId { get; set; }

    public bool DefaultOn { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
