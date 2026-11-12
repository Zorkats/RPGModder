namespace RPGModder.Core.Merge;

/// <summary>What <see cref="ReferenceMap.Scan"/> found in one mod: the numbers a remap cannot rewrite.</summary>
internal sealed class ModReferences
{
    /// <summary>Whole numbers in the change set's free text (GR-R9).</summary>
    public HashSet<long> FreeTextNumbers { get; } = new();

    /// <summary>Event ids named by event commands in a common event or a troop, which act on any map (3.11).</summary>
    public HashSet<long> AmbiguousEventIds { get; } = new();

    /// <summary>Constants Control Variables assigns; with a transfer by variable, one of them may be a map id.</summary>
    public HashSet<long> VariableConstants { get; } = new();

    /// <summary>Whole numbers in the plugin files the mod ships (GR-R11).</summary>
    public HashSet<long> PluginScriptNumbers { get; } = new();

    /// <summary>
    /// True when the mod sends the player or a vehicle to a map named by a variable (201 with <c>p[0]</c> 1, 202 with
    /// <c>p[1]</c> 1).
    /// </summary>
    public bool UsesVariableTransfer { get; set; }

    /// <summary>True when the change set refers to <paramref name="id"/> where a remap cannot follow it (GR-R9).</summary>
    public bool Blocks(IdKind kind, int id)
    {
        return FreeTextNumbers.Contains(id) || kind switch
        {
            IdKind.MapEvent => AmbiguousEventIds.Contains(id),
            IdKind.Map => UsesVariableTransfer && VariableConstants.Contains(id),
            _ => true
        };
    }

    /// <summary>True when <paramref name="id"/> appears in a shipped plugin file, so a move is only offered (GR-R11).</summary>
    public bool IsUnchecked(int id)
    {
        return PluginScriptNumbers.Contains(id);
    }
}
