namespace RPGModder.Core.Merge;

/// <summary>
/// An op that survives composition. <c>Op</c> is the contribution of the mod whose id sorts first;
/// <c>ModIds</c> lists every mod that contributed an equal op, sorted (GR-C8).
/// </summary>
public sealed record MergedOp(
    ChangeOp Op,
    IReadOnlyList<string> ModIds);
