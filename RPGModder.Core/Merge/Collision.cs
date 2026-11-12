namespace RPGModder.Core.Merge;

/// <summary>
/// Two or more mods that cannot both have their way. <c>Id</c> hashes the place and the mods but no value
/// (GR-X1), so a stored choice survives a mod update; <c>Fingerprint</c> hashes the values (GR-X2).
/// </summary>
public sealed record Collision(
    string Id,
    CollisionKind Kind,
    RecordKey Key,
    string Path,
    IReadOnlyList<string> ModIds,
    bool Blocking,
    IReadOnlyList<ResolutionKind> Allowed,
    string Fingerprint);
