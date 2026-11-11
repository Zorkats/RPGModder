namespace RPGModder.Core.Platform;

/// <summary>
/// What the store's app manifest says about an installed game: its ids, when the store last
/// updated it, its install state, and its installed depots (GR-V10, TX-11, TX-23).
/// </summary>
public sealed record StoreApp(
    string AppId,
    string? BuildId,
    DateTimeOffset? LastUpdated,
    int StateFlags,
    string AppManifestPath,
    IReadOnlyList<StoreDepot> Depots)
{
    /// <summary>The <c>StateFlags</c> value of a game that is fully installed and not updating.</summary>
    public const int FullyInstalled = 4;
}
