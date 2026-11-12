namespace RPGModder.Core.Merge;

/// <summary>What <see cref="ReferenceMap.Scan"/> found in one mod: the numbers a remap cannot rewrite.</summary>
internal sealed class ModReferences
{
    private readonly Dictionary<IdKind, HashSet<long>> _ambiguous = new();
    private readonly HashSet<IdKind> _unknownIds = new();
    private readonly Dictionary<IdKind, HashSet<long>> _used = new();

    /// <summary>Whole numbers in the change set's free text (GR-R9).</summary>
    public HashSet<long> FreeTextNumbers { get; } = new();

    /// <summary>Constants Control Variables assigns; with a by-variable command, one of them may be its id.</summary>
    public HashSet<long> VariableConstants { get; } = new();

    /// <summary>Random ranges Control Variables assigns, low end first; with a by-variable command, any id in one may be its id.</summary>
    public List<(long Min, long Max)> VariableRanges { get; } = new();

    /// <summary>Whole numbers in the plugin files the mod ships (GR-R11).</summary>
    public HashSet<long> PluginScriptNumbers { get; } = new();

    /// <summary>
    /// The kinds the mod names through a variable: a map (201 with <c>p[0]</c> 1, 202 with <c>p[1]</c> 1), a troop
    /// (301 with <c>p[0]</c> 1), an actor (311 to 318 and 326 with <c>p[0]</c> 1).
    /// </summary>
    public HashSet<IdKind> ByVariableKinds { get; } = new();

    /// <summary>
    /// Records an id that may be of <paramref name="kind"/> where a remap cannot tell (3.11): an event command outside
    /// a map, the second equip slot of a dual-wield game, a field whose kind sits in a vanilla sibling.
    /// </summary>
    public void AddAmbiguous(IdKind kind, long id)
    {
        if (!_ambiguous.TryGetValue(kind, out HashSet<long>? ids))
        {
            ids = new HashSet<long>();
            _ambiguous.Add(kind, ids);
        }

        ids.Add(id);
    }

    /// <summary>
    /// Records that some id of <paramref name="kind"/> that only vanilla holds is named where a remap cannot see it:
    /// a field whose deciding sibling the mod changed alone, such as a drop whose <c>kind</c> changed and whose
    /// <c>dataId</c> did not.
    /// </summary>
    public void AddUnknownId(IdKind kind)
    {
        _unknownIds.Add(kind);
    }

    /// <summary>Records an id of <paramref name="kind"/> the change set names where a remap rewrites it.</summary>
    public void AddUsed(IdKind kind, long id)
    {
        if (!_used.TryGetValue(kind, out HashSet<long>? ids))
        {
            ids = new HashSet<long>();
            _used.Add(kind, ids);
        }

        ids.Add(id);
    }

    /// <summary>
    /// True when the change set may refer to <paramref name="id"/> of <paramref name="kind"/>: a place a remap
    /// rewrites, a place of no known kind, or free text (GR-R7).
    /// </summary>
    public bool Uses(IdKind kind, int id)
    {
        return FreeTextNumbers.Contains(id) ||
               (_used.TryGetValue(kind, out HashSet<long>? ids) && ids.Contains(id)) ||
               (_ambiguous.TryGetValue(kind, out HashSet<long>? unknown) && unknown.Contains(id)) ||
               ByVariable(kind, id);
    }

    /// <summary>True when the change set refers to <paramref name="id"/> where a remap cannot follow it (GR-R9).</summary>
    public bool Blocks(IdKind kind, int id)
    {
        return FreeTextNumbers.Contains(id) ||
               !RpgDataSchema.IsRemappable(kind) ||
               (_ambiguous.TryGetValue(kind, out HashSet<long>? ids) && ids.Contains(id)) ||
               _unknownIds.Contains(kind) ||
               ByVariable(kind, id);
    }

    /// <summary>True when a by-variable command of <paramref name="kind"/> may get <paramref name="id"/> from a constant or a random range.</summary>
    private bool ByVariable(IdKind kind, int id)
    {
        return ByVariableKinds.Contains(kind) &&
               (VariableConstants.Contains(id) || VariableRanges.Exists(range => range.Min <= id && id <= range.Max));
    }

    /// <summary>True when <paramref name="id"/> appears in a shipped plugin file, so a move is only offered (GR-R11).</summary>
    public bool IsUnchecked(int id)
    {
        return PluginScriptNumbers.Contains(id);
    }
}
