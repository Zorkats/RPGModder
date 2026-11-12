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
    /// moved id is not moved twice. Each applied entry adds <c>rules.remap_applied</c> (GR-R13). Step 1 applies the
    /// <see cref="IdKind.MapEvent"/> and <see cref="IdKind.Map"/> kinds and ignores the others.
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
        List<IdRemap> active = remaps
            .Where(remap => remap.ModId == changeSet.ModId && remap.Kind is IdKind.MapEvent or IdKind.Map && AddedIdIndex.IsActive(remap, added))
            .Distinct()
            .OrderBy(remap => remap.Kind)
            .ThenBy(remap => remap.Scope, StringComparer.Ordinal)
            .ThenBy(remap => remap.FromId)
            .ToList();

        var table = new RemapTable();
        foreach (IdRemap remap in active)
        {
            Dictionary<int, int> moves = remap.Kind == IdKind.Map ? table.Maps : EventMoves(table, remap.Scope);
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
        List<ChangeOp> ops = changeSet.Ops
            .Select(op => ReferenceMap.Rewrite(op, table))
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

    /// <summary>The mod, kind, old id and new id GR-R13 asks for, with the map file of an event.</summary>
    private static string Describe(IdRemap remap)
    {
        string where = remap.Kind == IdKind.MapEvent ? " in " + remap.Scope : "";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Mod {remap.ModId}: {remap.Kind} {remap.FromId}{where} moved to {remap.ToId}.");
    }

    private static Dictionary<int, int> EventMoves(RemapTable table, string scope)
    {
        if (!table.Events.TryGetValue(scope, out Dictionary<int, int>? moves))
        {
            moves = new Dictionary<int, int>();
            table.Events.Add(scope, moves);
        }

        return moves;
    }
}
