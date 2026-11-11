using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// One stored answer to a collision. <see cref="Kind"/> is kept as the text the merge engine's
/// resolution kind writes (for example <c>PickMod</c> or <c>Gate</c>), so this file has no
/// dependency on <c>Merge</c>.
/// </summary>
public sealed class ProfileResolution
{
    public string CollisionId { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? ModId { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();
}
