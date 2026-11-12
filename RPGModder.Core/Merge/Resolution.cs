namespace RPGModder.Core.Merge;

/// <summary>
/// A player's stored choice. <c>Fingerprint</c> is the collision's fingerprint when the choice was made; it lets
/// <see cref="Composer"/> tell a reused choice from an exact one (GR-X2). <c>null</c> means "not recorded".
/// </summary>
public sealed record Resolution(
    string CollisionId,
    ResolutionKind Kind,
    string? ModId,
    string? Fingerprint = null);
