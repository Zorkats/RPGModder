namespace RPGModder.Core.Merge;

/// <summary>One mod's moves, old id to new id, applied together so no move feeds another.</summary>
internal sealed class RemapTable
{
    /// <summary>The moves of every kind but <see cref="IdKind.MapEvent"/>, whose ids are one per map.</summary>
    public Dictionary<IdKind, Dictionary<int, int>> Global { get; } = new();

    /// <summary>Event moves by map file key.</summary>
    public Dictionary<string, Dictionary<int, int>> Events { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Global.Values.All(moves => moves.Count == 0) && Events.Values.All(moves => moves.Count == 0);

    /// <summary>The moves of <paramref name="kind"/>, or <c>null</c> when none of that kind moves.</summary>
    public Dictionary<int, int>? MovesOf(IdKind kind)
    {
        return Global.GetValueOrDefault(kind);
    }

    /// <summary>The moves an entry of <paramref name="kind"/> and <paramref name="scope"/> joins, made on first use.</summary>
    public Dictionary<int, int> For(IdKind kind, string scope)
    {
        if (kind == IdKind.MapEvent)
        {
            return Events.TryGetValue(scope, out Dictionary<int, int>? events) ? events : Events[scope] = new Dictionary<int, int>();
        }

        return Global.TryGetValue(kind, out Dictionary<int, int>? moves) ? moves : Global[kind] = new Dictionary<int, int>();
    }
}
