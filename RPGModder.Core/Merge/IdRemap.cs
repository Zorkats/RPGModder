namespace RPGModder.Core.Merge;

/// <summary>
/// One mod's added id moved to another (guide 3.11). <c>Scope</c> is the map file key for <see cref="IdKind.MapEvent"/>
/// (<c>data/map012.json</c>) and empty for every other kind. The profile keeps these append-only (GR-R12).
/// </summary>
public sealed record IdRemap(
    string ModId,
    IdKind Kind,
    string Scope,
    int FromId,
    int ToId);
