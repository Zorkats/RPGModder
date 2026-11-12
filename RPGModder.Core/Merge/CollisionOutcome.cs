namespace RPGModder.Core.Merge;

public sealed record CollisionOutcome(
    string CollisionId,
    ResolutionSource Source,
    ResolutionKind? Kind,
    string? ModId);
