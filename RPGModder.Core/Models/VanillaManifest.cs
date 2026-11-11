using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

/// <summary>
/// <c>.rpgmodder/backups/vanilla.manifest.json</c>: what the vanilla backup holds and how far it is
/// trusted. Its presence means the backup is complete, not that it is clean (GR-V6, GR-V10, TX-21, TX-24).
/// </summary>
public sealed class VanillaManifest
{
    [JsonProperty("created_utc")]
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Null when the game is not a Steam install, or when the backup was migrated without proof (GR-V10).</summary>
    [JsonProperty("steam_app_id")]
    public string? SteamAppId { get; set; }

    [JsonProperty("steam_build_id")]
    public string? SteamBuildId { get; set; }

    /// <summary>Every installed depot and its manifest id, when the backup was proven against the store's list.</summary>
    [JsonProperty("steam_depots")]
    public List<VanillaDepot>? SteamDepots { get; set; }

    /// <summary><see cref="VanillaBaseline.StoreManifest"/> or <see cref="VanillaBaseline.Workflow"/>.</summary>
    [JsonProperty("baseline_source")]
    public string BaselineSource { get; set; } = VanillaBaseline.Workflow;

    /// <summary><see cref="VanillaBaseline.Confirmed"/>, <see cref="VanillaBaseline.Unverified"/> or <see cref="VanillaBaseline.PlayerModified"/>.</summary>
    [JsonProperty("baseline")]
    public string Baseline { get; set; } = VanillaBaseline.Confirmed;

    /// <summary>When the player chose "Keep this backup" for an unverified backup (GR-V6).</summary>
    [JsonProperty("baseline_accepted_utc")]
    public DateTimeOffset? BaselineAcceptedUtc { get; set; }

    /// <summary>For a <see cref="VanillaBaseline.PlayerModified"/> base, how it differs from the store's files (TX-24).</summary>
    [JsonProperty("base_differences")]
    public BaselineCounts? BaseDifferences { get; set; }

    [JsonProperty("audit")]
    public BaselineAudit? Audit { get; set; }

    [JsonProperty("files")]
    public List<VanillaFileEntry> Files { get; set; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    /// <summary>Whether deploys must stop until the player chooses (GR-V6).</summary>
    [JsonIgnore]
    public bool BlocksDeploy => Baseline == VanillaBaseline.Unverified && BaselineAcceptedUtc == null;

    /// <summary>Whether every plan carries <c>workspace.baseline_unverified</c> as a warning (GR-V6, TX-24).</summary>
    [JsonIgnore]
    public bool WarnsOnDeploy => Baseline != VanillaBaseline.Confirmed && !BlocksDeploy;
}
