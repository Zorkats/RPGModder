using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// The only code that changes an id (GR-K3, GR-R1). It runs after <see cref="ChangeSetBuilder"/> and before
/// <see cref="Composer"/>, one mod at a time, and returns a new change set.
/// </summary>
public static class IdRemapper
{
    /// <summary>
    /// Applies the entries of <paramref name="remaps"/> that name this mod and an id it still adds; the others are
    /// inactive and change nothing (GR-R12). All moves of one mod apply in one pass, so an id moved onto another
    /// moved id is not moved twice. Each applied entry adds <c>rules.remap_applied</c> (GR-R13). Map events, maps, the
    /// twelve database kinds, switches and variables move; an entry of another kind changes nothing. A switch or
    /// variable entry is active while the mod still names the old id; its name in <c>System.json</c> moves with it,
    /// and the mod gets an empty name at the new id when it sets none there, so the list of names reaches the new id
    /// (the game ignores an id past its end).
    /// </summary>
    public static ModChangeSet Apply(ModChangeSet changeSet, IReadOnlyList<IdRemap> remaps)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(remaps);

        if (!remaps.Any(remap => remap.ModId == changeSet.ModId))
        {
            return changeSet;
        }

        IReadOnlyList<AddedId> added = AddedIdIndex.Of(changeSet);
        // Read as MV, whose rows are a superset: the planner already left out an entry its engine finds out of force,
        // so this only keeps the entries the plan holds.
        var references = new Lazy<ModReferences>(() => ReferenceMap.Scan(changeSet, [], dualWield: true, mz: false));
        List<IdRemap> active = remaps
            .Where(remap => remap.ModId == changeSet.ModId && RpgDataSchema.IsRemappable(remap.Kind) && InRange(remap) &&
                            IdRemapPlanner.IsActive(remap, added, () => references.Value))
            .Distinct()
            .OrderBy(remap => remap.Kind)
            .ThenBy(remap => remap.Scope, StringComparer.Ordinal)
            .ThenBy(remap => remap.FromId)
            .ToList();

        var table = new RemapTable();
        foreach (IdRemap remap in active)
        {
            Dictionary<int, int> moves = table.For(remap.Kind, remap.Scope);
            if (remap.ToId <= 0 || moves.ContainsValue(remap.ToId) || !moves.TryAdd(remap.FromId, remap.ToId))
            {
                // The planner gives each old id one positive target of its own; anything else is a caller bug.
                throw new ArgumentException(
                    $"The remaps for mod {changeSet.ModId} do not give {remap.Kind} {remap.FromId} a single target of its own.",
                    nameof(remaps));
            }
        }

        if (table.IsEmpty)
        {
            return changeSet;
        }

        // A stable sort: two ops can share a place when a recorded move lands on an id the mod now adds too.
        IReadOnlyList<ChangeOp> rewritten = KeepOwnNames(changeSet.Ops, ReferenceMap.Rewrite(changeSet, table));
        List<ChangeOp> ops = rewritten
            .Concat(NameSlots(changeSet.ModId, active, rewritten))
            .OrderBy(op => op, Comparer<ChangeOp>.Create(ChangeSetBuilder.CompareOps))
            .ToList();

        var diagnostics = changeSet.Diagnostics.ToList();
        diagnostics.AddRange(active.Select(remap => new OperationDiagnostic(
            DiagnosticCodes.RulesRemapApplied,
            Describe(remap),
            DiagnosticSeverity.Info,
            remap.ModId)));

        return changeSet with { Ops = ops, Diagnostics = diagnostics };
    }

    /// <summary>
    /// The owner, 2026-11-17: a name a recorded entry moves onto a slot the mod's own file names (its new id there
    /// could not move, so both meanings share the id and the plan warns) gives way to the file's name, so the mod does
    /// not clash with itself. An empty name in the file is only padding and gives way to the moved one later.
    /// </summary>
    private static IReadOnlyList<ChangeOp> KeepOwnNames(IReadOnlyList<ChangeOp> original, IReadOnlyList<ChangeOp> rewritten)
    {
        RecordKey system = RemapPlaces.StateKey;
        List<(ChangeOp Before, ChangeOp After)> pairs = original.Zip(rewritten).ToList();
        var ownNames = new HashSet<string>(
            pairs.Where(pair => pair.After.Key == system && pair.After.Path == pair.Before.Path &&
                                pair.After.Value is JValue { Value: string { Length: > 0 } })
                .Select(pair => pair.After.Path),
            StringComparer.Ordinal);
        if (ownNames.Count == 0)
        {
            return rewritten;
        }

        return pairs
            .Where(pair => pair.After.Key != system || pair.After.Path == pair.Before.Path || !ownNames.Contains(pair.After.Path))
            .Select(pair => pair.After)
            .ToList();
    }

    /// <summary>A switch or variable target from 1 to 5000 on any engine (GR-R6); a database target is checked by the planner, which knows the engine.</summary>
    private static bool InRange(IdRemap remap)
    {
        return !RpgDataSchema.IsStateKind(remap.Kind) || (remap.ToId >= 1 && remap.ToId <= RpgDataSchema.RemapLimit(remap.Kind, null));
    }

    /// <summary>
    /// An empty name at each new switch or variable id the rewritten ops set no name for: <c>Game_Switches</c> and
    /// <c>Game_Variables</c> ignore an id past the end of <c>System.json</c>'s list.
    /// </summary>
    private static IEnumerable<ChangeOp> NameSlots(string modId, List<IdRemap> active, IReadOnlyList<ChangeOp> rewritten)
    {
        RecordKey system = RemapPlaces.StateKey;
        var named = new HashSet<string>(
            rewritten.Where(op => op.Key == system).Select(op => op.Path),
            StringComparer.Ordinal);
        bool wholeFile = rewritten.Any(op => op.Key == system && op.Path.Length == 0);
        foreach (IdRemap remap in active.Where(item => RpgDataSchema.IsStateKind(item.Kind)))
        {
            string property = RpgDataSchema.StateNamesOf(remap.Kind)!;
            string path = MergePath.Index(property, remap.ToId);
            if (!wholeFile && !named.Contains(property) && named.Add(path))
            {
                yield return new ChangeOp(modId, system, path, ChangeKind.SetLeaf, new JValue(""), null);
            }
        }
    }

    /// <summary>The mod, kind, old id and new id GR-R13 asks for, with the map file of an event.</summary>
    private static string Describe(IdRemap remap)
    {
        string where = remap.Kind == IdKind.MapEvent ? " in " + remap.Scope : "";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Mod {remap.ModId}: {remap.Kind} {remap.FromId}{where} moved to {remap.ToId}.");
    }
}
