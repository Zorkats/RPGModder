using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>Switches and variables (guide 3.11, step 3): shared ids are found and a move is only ever offered.</summary>
public static partial class IdRemapPlanner
{
    private static readonly IdKind[] StateKinds = [IdKind.Switch, IdKind.Variable];

    /// <summary>
    /// GR-R15 for switches and variables: an id two enabled mods use and vanilla does not is shared, and the plan lists
    /// it once (<see cref="RemapPlan.SharedStateIds"/>). Sharing may be meant, so no shared id moves on its own: each party
    /// GR-R10 would move is offered a move of every id it must take along, on consecutive targets (GR-R14). A party
    /// moves only when its range holds no vanilla id, no id it has a recorded entry for, and no id it names where a
    /// move cannot follow (GR-R9). With no scan of vanilla, nothing is shared.
    /// </summary>
    private static void PlanStates(StateInputs inputs, StateResult result)
    {
        if (inputs.Vanilla is null)
        {
            return;
        }

        foreach (IdKind kind in StateKinds)
        {
            int limit = RpgDataSchema.RemapLimit(kind, inputs.Engine) ?? 0;
            var vanillaIds = new HashSet<int>(inputs.Vanilla.Of(kind));
            var claims = new List<StateClaim>();
            foreach (RemapMod mod in inputs.Mods.Where(item => item.Enabled))
            {
                string modId = mod.ChangeSet.ModId;
                bool deployed = inputs.Deployed.Contains(modId);
                ModReferences references = inputs.ReferencesOf(mod);
                foreach (int id in references.StateIds(kind, limit))
                {
                    // An entry in force uses its target even when vanilla has come to use the old id.
                    bool pinned = inputs.Pins.TryGetValue((modId, kind, "", id), out int toId);
                    if (pinned)
                    {
                        claims.Add(new StateClaim(mod, id, toId, Pinned: true, deployed));
                    }

                    // A range the entries leave behind still reads the old id (GR-R14), so the mod uses that too.
                    if (!vanillaIds.Contains(id) && (!pinned || SplitsRange(new IdRemap(modId, kind, "", id, toId), references, inputs.Pins)))
                    {
                        claims.Add(new StateClaim(mod, id, id, Pinned: false, deployed));
                    }
                }
            }

            MoveOwnTargets(claims, kind, vanillaIds, inputs, limit, result);

            // One offer per mod and range, however many shared ids the range holds.
            var offered = new HashSet<(string ModId, int Low)>();
            foreach (IGrouping<int, StateClaim> shared in claims.GroupBy(claim => claim.Effective).OrderBy(group => group.Key))
            {
                List<StateClaim> parties = shared
                    .DistinctBy(claim => claim.Mod.ChangeSet.ModId)
                    .OrderBy(claim => claim.Rank)
                    .ThenBy(claim => claim.Mod.ChangeSet.Priority)
                    .ThenBy(claim => claim.Mod.ChangeSet.ModId, StringComparer.Ordinal)
                    .ToList();
                if (parties.Count < 2)
                {
                    continue;
                }

                result.Shared.Add(new SharedStateId(
                    kind,
                    shared.Key,
                    parties.Select(claim => claim.Mod.ChangeSet.ModId).Order(StringComparer.Ordinal).ToList()));

                // GR-R10: every party that cannot move stays; when every party can, the first in order stays.
                var closures = parties.ToDictionary(claim => claim, claim => claim.Pinned ? null : Closure(claim, kind, vanillaIds, inputs, limit));
                List<StateClaim> staying = parties.Where(claim => closures[claim] is null).ToList();
                if (staying.Count == 0)
                {
                    staying.Add(parties[0]);
                }

                foreach (StateClaim mover in parties.Except(staying))
                {
                    (int low, int high) = closures[mover]!.Value;
                    if (offered.Add((mover.Mod.ChangeSet.ModId, low)))
                    {
                        Offer(mover, kind, low, high, inputs, result);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A mod whose new switch or variable is the target of its own entry in force would read one id for two
    /// meanings. The new id moves, with every id its ranges take along, onto consecutive free ids, as a new entry
    /// (the owner, 2026-11-12; GR-R10 moves a record so). When it cannot, the two meanings share the id and the plan
    /// warns with <c>rules.remap_stale_reference</c>. When a plugin file the mod ships names one of the ids, the move
    /// is only offered (<see cref="OfferOwn"/>).
    /// </summary>
    private static void MoveOwnTargets(List<StateClaim> claims, IdKind kind, HashSet<int> vanillaIds, StateInputs inputs, int limit, StateResult result)
    {
        foreach (IGrouping<string, StateClaim> mine in claims.GroupBy(claim => claim.Mod.ChangeSet.ModId).ToList())
        {
            var targets = mine.Where(claim => claim.Pinned).ToDictionary(claim => claim.Effective, claim => claim.Id);
            var moved = new HashSet<int>();
            var offered = new HashSet<int>();
            foreach (StateClaim own in mine.Where(claim => !claim.Pinned && targets.ContainsKey(claim.Id)).OrderBy(claim => claim.Id))
            {
                if (moved.Contains(own.Id) || offered.Contains(own.Id))
                {
                    continue;
                }

                (int Low, int High)? run = Closure(own, kind, vanillaIds, inputs, limit);
                int? start = run is null ? null : inputs.Targets.TakeRun(kind, "", run.Value.High - run.Value.Low + 1);
                if (start is null)
                {
                    result.Diagnostics.Add(new OperationDiagnostic(
                        DiagnosticCodes.RulesRemapStaleReference,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Mod {mine.Key}: {kind} {own.Id} is new in the mod and is also where its recorded entry moved {kind} {targets[own.Id]}, and the new id cannot move, so both use {kind} {own.Id}."),
                        DiagnosticSeverity.Warning,
                        mine.Key));
                    continue;
                }

                List<IdRemap> group = Enumerable.Range(run!.Value.Low, run.Value.High - run.Value.Low + 1)
                    .Select(id => new IdRemap(mine.Key, kind, "", id, start.Value + (id - run.Value.Low)))
                    .ToList();
                ModReferences references = inputs.ReferencesOf(own.Mod);
                if (group.Any(remap => references.IsUnchecked(remap.FromId)))
                {
                    OfferOwn(own, targets[own.Id], group, claims, result);
                    offered.UnionWith(group.Select(remap => remap.FromId));
                    continue;
                }

                result.Moves.AddRange(group);
                moved.UnionWith(group.Select(remap => remap.FromId));
            }

            // Fresh targets are free, so the moved ids share nothing any more.
            claims.RemoveAll(claim => claim.Mod.ChangeSet.ModId == mine.Key && !claim.Pinned && moved.Contains(claim.Id));
        }
    }

    /// <summary>
    /// GR-R11 (the owner, 2026-11-17): a plugin file the mod ships holds a number of <paramref name="group"/>, so the
    /// move of the mod's new id off its own entry's target is only offered. Until the player takes it, both meanings
    /// share the id; when no other mod uses the id, that is a <c>StateIdOverlap</c> of the mod alone, which carries
    /// the offer. When another mod does, the overlap the mods share carries it.
    /// </summary>
    private static void OfferOwn(StateClaim own, int entryFrom, List<IdRemap> group, List<StateClaim> claims, StateResult result)
    {
        string modId = own.Mod.ChangeSet.ModId;
        result.Offers.AddRange(group);
        result.Groups.Add(group);
        if (!claims.Any(claim => claim.Effective == own.Id && claim.Mod.ChangeSet.ModId != modId))
        {
            result.Shared.Add(new SharedStateId(group[0].Kind, own.Id, [modId]));
        }

        result.Diagnostics.Add(new OperationDiagnostic(
            DiagnosticCodes.RulesRemapUnchecked,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Mod {modId}: {group[0].Kind} {own.Id} is new in the mod and is also where its recorded entry moved {group[0].Kind} {entryFrom}. It can move to {group[0].ToId} onward, but a plugin file the mod ships contains one of those numbers, so the move is only offered."),
            DiagnosticSeverity.Warning,
            modId));
    }

    /// <summary>
    /// The ids a move of <paramref name="claim"/>'s id must take along: every range of the mod that holds one of them,
    /// until none adds more (GR-R14). <c>null</c> when one of them cannot move.
    /// </summary>
    private static (int Low, int High)? Closure(StateClaim claim, IdKind kind, HashSet<int> vanillaIds, StateInputs inputs, int limit)
    {
        ModReferences references = inputs.ReferencesOf(claim.Mod);
        List<(int First, int Last)> ranges = references.RangesOf(kind).Where(range => range.Last >= Math.Max(range.First, 1)).ToList();
        int low = claim.Id;
        int high = claim.Id;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((int first, int last) in ranges)
            {
                int start = Math.Max(first, 1);
                if (start <= high && last >= low && (start < low || last > high))
                {
                    low = Math.Min(low, start);
                    high = Math.Max(high, last);
                    grew = true;
                }
            }
        }

        if (high > limit)
        {
            return null;
        }

        string modId = claim.Mod.ChangeSet.ModId;
        for (int id = low; id <= high; id++)
        {
            if (vanillaIds.Contains(id) || inputs.Pins.ContainsKey((modId, kind, "", id)) || references.Blocks(kind, id))
            {
                return null;
            }
        }

        return (low, high);
    }

    /// <summary>
    /// Offers a move of every id from <paramref name="low"/> to <paramref name="high"/> onto consecutive free ids, in
    /// order, unless that passes the limit (GR-R6). A number in a shipped plugin file makes the offer carry
    /// <c>rules.remap_unchecked</c> (GR-R11).
    /// </summary>
    private static void Offer(StateClaim mover, IdKind kind, int low, int high, StateInputs inputs, StateResult result)
    {
        int? start = inputs.Targets.TakeRun(kind, "", high - low + 1);
        if (start is null)
        {
            return;
        }

        string modId = mover.Mod.ChangeSet.ModId;
        List<IdRemap> group = Enumerable.Range(low, high - low + 1)
            .Select(id => new IdRemap(modId, kind, "", id, start.Value + (id - low)))
            .ToList();
        result.Offers.AddRange(group);
        result.Groups.Add(group);

        ModReferences references = inputs.ReferencesOf(mover.Mod);
        if (group.Any(remap => references.IsUnchecked(remap.FromId)))
        {
            string ids = low == high
                ? low.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{low} to {high}");
            result.Diagnostics.Add(new OperationDiagnostic(
                DiagnosticCodes.RulesRemapUnchecked,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Mod {modId}: {kind} {ids} can move to {start.Value} onward to stop sharing an id with another mod, but a plugin file the mod ships contains one of those numbers."),
                DiagnosticSeverity.Warning,
                modId));
        }
    }

    /// <summary>
    /// GR-R14 for an entry in force: a range of the mod that holds its old id still reads the old ids unless the mod's
    /// entries move the whole range onto consecutive ids, in order.
    /// </summary>
    private static bool SplitsRange(IdRemap remap, ModReferences references, Dictionary<(string ModId, IdKind Kind, string Scope, int FromId), int> pins)
    {
        if (!RpgDataSchema.IsStateKind(remap.Kind))
        {
            return false;
        }

        foreach ((int first, int last) in references.RangesOf(remap.Kind))
        {
            int low = Math.Max(first, 1);
            if (remap.FromId < low || remap.FromId > last)
            {
                continue;
            }

            if ((long)last - low + 1 > pins.Count || !pins.TryGetValue((remap.ModId, remap.Kind, remap.Scope, low), out int start))
            {
                return true;
            }

            for (int id = low + 1; id <= last; id++)
            {
                if (!pins.TryGetValue((remap.ModId, remap.Kind, remap.Scope, id), out int target) || target != start + (id - low))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The highest switch or variable id an installed mod names, enabled or not, so no target lands on one.</summary>
    private static int HighestStateId(IEnumerable<RemapMod> mods, IdKind kind, Func<RemapMod, ModReferences> referencesOf, int limit)
    {
        int highest = 0;
        foreach (RemapMod mod in mods)
        {
            SortedSet<int> ids = referencesOf(mod).StateIds(kind, limit);
            highest = Math.Max(highest, ids.Count == 0 ? 0 : ids.Max);
        }

        return highest;
    }

    private sealed record StateInputs(
        List<RemapMod> Mods,
        Dictionary<(string ModId, IdKind Kind, string Scope, int FromId), int> Pins,
        HashSet<string> Deployed,
        VanillaStateIds? Vanilla,
        Func<RemapMod, ModReferences> ReferencesOf,
        Targets Targets,
        RpgMakerEngine? Engine);

    private sealed class StateResult
    {
        /// <summary>Moves the plan makes on its own: a mod's new ids off its own entries' targets.</summary>
        public List<IdRemap> Moves { get; } = new();

        public List<IdRemap> Offers { get; } = new();

        public List<IReadOnlyList<IdRemap>> Groups { get; } = new();

        public List<SharedStateId> Shared { get; } = new();

        public List<OperationDiagnostic> Diagnostics { get; } = new();
    }

    /// <summary>One enabled mod's use of a switch or variable vanilla does not use, at the id it has after its recorded entry.</summary>
    private sealed record StateClaim(RemapMod Mod, int Id, int Effective, bool Pinned, bool Deployed)
    {
        /// <summary>GR-R4: a recorded entry, then a mod in <c>DeployedMods</c>, then the rest.</summary>
        public int Rank => Pinned ? 0 : Deployed ? 1 : 2;
    }
}
