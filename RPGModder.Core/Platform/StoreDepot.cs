namespace RPGModder.Core.Platform;

/// <summary>An installed depot and the id of its manifest, as the store's app manifest names them (GR-V10).</summary>
public sealed record StoreDepot(string DepotId, string ManifestId);
