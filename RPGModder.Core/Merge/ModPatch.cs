using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Merge;

/// <summary>One <c>json_patches</c> entry.</summary>
public sealed record ModPatch(
    string Target,
    JObject MergeData);
