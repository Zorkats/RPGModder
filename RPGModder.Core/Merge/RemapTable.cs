namespace RPGModder.Core.Merge;

/// <summary>One mod's moves, old id to new id, applied together so no move feeds another.</summary>
internal sealed class RemapTable
{
    public Dictionary<int, int> Maps { get; } = new();

    /// <summary>Event moves by map file key.</summary>
    public Dictionary<string, Dictionary<int, int>> Events { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Maps.Count == 0 && Events.Count == 0;
}
