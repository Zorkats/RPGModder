namespace RPGModder.Core.Models;

/// <summary>The values of <c>baseline</c> and <c>baseline_source</c> in <c>vanilla.manifest.json</c>.</summary>
public static class VanillaBaseline
{
    /// <summary>Proven against the store's list, or built by the first-run or rebuild workflow.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Migrated from workspace schema 3 without proof (GR-V6).</summary>
    public const string Unverified = "unverified";

    /// <summary>A base the player chose to keep although it differs from the store's files (TX-24).</summary>
    public const string PlayerModified = "player_modified";

    /// <summary>Every file was proven against the store's list (TX-23).</summary>
    public const string StoreManifest = "store_manifest";

    /// <summary>Built or migrated without the store's list.</summary>
    public const string Workflow = "workflow";
}
