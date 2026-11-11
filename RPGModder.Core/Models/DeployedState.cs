using Newtonsoft.Json;

namespace RPGModder.Core.Models;

/// <summary>
/// What the last successful deploy put into the game folder, stored as <c>.rpgmodder/deployed.json</c>.
/// <see cref="Files"/> is null while the deploy cannot list the files it wrote; a later version fills it
/// and verifies a null list against the vanilla manifest on its first deploy.
/// </summary>
public sealed record DeployedState(
    [property: JsonProperty("schema_version")] int SchemaVersion,
    [property: JsonProperty("profile")] string Profile,
    [property: JsonProperty("enabled_mods")] IReadOnlyList<string> EnabledMods,
    [property: JsonProperty("plan_hash")] string PlanHash,
    [property: JsonProperty("deployed_utc")] string DeployedUtc,
    [property: JsonProperty("files")] IReadOnlyList<string>? Files)
{
    public const int CurrentSchemaVersion = 1;
}
