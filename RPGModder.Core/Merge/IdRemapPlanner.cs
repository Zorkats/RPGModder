using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// Decides which added ids move before composition (guide 3.11, GR-R3 to GR-R12). Step 1 plans the
/// <see cref="IdKind.MapEvent"/> and <see cref="IdKind.Map"/> kinds. The result does not depend on the order of any
/// input.
/// </summary>
public static class IdRemapPlanner
{
    /// <param name="installed">Every installed mod, enabled or not, once each.</param>
    /// <param name="recorded">The profile's <c>Remaps</c>, every entry, active or not.</param>
    /// <param name="deployedMods">The profile's <c>DeployedMods</c> (GR-R4).</param>
    /// <param name="baseFiles">
    /// Vanilla's files, for the ids vanilla uses (GR-R5): the map file of every map that gets an event, and, when a
    /// mod adds a map, <c>data/MapInfos.json</c> and every vanilla map file, listed in the tree or not. A clash whose
    /// base file is missing cannot move, and a map id whose file vanilla has is never a target and never moves.
    /// </param>
    public static RemapPlan Plan(
        IReadOnlyList<RemapMod> installed,
        IReadOnlyList<IdRemap> recorded,
        IReadOnlyCollection<string> deployedMods,
        BaseFileSet baseFiles)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(deployedMods);
        ArgumentNullException.ThrowIfNull(baseFiles);

        List<RemapMod> mods = installed.OrderBy(mod => mod.ChangeSet.ModId, StringComparer.Ordinal).ToList();
        for (int index = 1; index < mods.Count; index++)
        {
            if (mods[index].ChangeSet.ModId == mods[index - 1].ChangeSet.ModId)
            {
                throw new ArgumentException($"Two installed mods share the mod id {mods[index].ChangeSet.ModId}.", nameof(installed));
            }
        }

        var added = mods.ToDictionary(mod => mod.ChangeSet.ModId, mod => AddedIdIndex.Of(mod.ChangeSet), StringComparer.Ordinal);
        var deployed = new HashSet<string>(deployedMods, StringComparer.Ordinal);
        var diagnostics = new List<OperationDiagnostic>();
        Dictionary<(string ModId, IdKind Kind, string Scope, int FromId), int> pins = Pins(mods, added, recorded, baseFiles, diagnostics);

        var claims = new List<Claim>();
        foreach (RemapMod mod in mods.Where(item => item.Enabled))
        {
            string modId = mod.ChangeSet.ModId;
            foreach (AddedId id in added[modId])
            {
                bool pinned = pins.TryGetValue((modId, id.Kind, id.Scope, id.Id), out int toId);
                claims.Add(new Claim(mod, id, pinned ? toId : id.Id, pinned, deployed.Contains(modId)));
            }
        }

        var targets = new Targets(added.Values.SelectMany(list => list).ToList(), recorded, baseFiles);
        var references = new Dictionary<string, ModReferences>(StringComparer.Ordinal);
        var moves = new List<IdRemap>();
        var offers = new List<IdRemap>();
        IEnumerable<IGrouping<(IdKind Kind, string Scope, int Id), Claim>> groups = claims
            .GroupBy(claim => (Kind: claim.Added.Kind, Scope: claim.Added.Scope, Id: claim.Effective))
            .OrderBy(group => group.Key.Kind)
            .ThenBy(group => group.Key.Scope, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Id);
        foreach (IGrouping<(IdKind Kind, string Scope, int Id), Claim> group in groups)
        {
            List<Party> parties = Partition(group);
            if (parties.Count < 2)
            {
                continue;
            }

            (IdKind kind, string scope, _) = group.Key;
            foreach (Party party in parties)
            {
                party.Classify(kind, claim => ReferencesOf(claim.Mod, references), claim => IsVanillaMapFile(kind, claim.Added.Id, baseFiles));
            }

            // GR-R4 and GR-R10: the parties that cannot move stay; when every party can, the first in order stays.
            List<Party> staying = parties.Where(party => !party.CanMove).ToList();
            if (staying.Count == 0)
            {
                staying.Add(parties[0]);
            }
            else if (staying.All(party => party.IsUnchecked))
            {
                // GR-R4 before GR-R11 (the owner, 2026-11-12): a party in DeployedMods does not move to make room
                // for parties only a plugin file holds. It stays too, and the player is offered their move.
                staying.AddRange(parties.Except(staying).Where(party => party.Rank == 1));
            }

            foreach (Party party in parties.Except(staying).ToList())
            {
                int? target = targets.Take(kind, scope);
                if (target is null)
                {
                    // GR-R6: past the editor's limit, or vanilla's ids are unknown; the clash stays a choice.
                    staying.Add(party);
                    continue;
                }

                moves.AddRange(party.Claims.Select(claim => new IdRemap(claim.Mod.ChangeSet.ModId, kind, scope, claim.Added.Id, target.Value)));
            }

            if (staying.Count < 2)
            {
                continue;
            }

            // GR-R11: a party held only by a number in a shipped plugin file is offered a move of its own.
            foreach (Party party in staying.Where(item => item.IsUnchecked))
            {
                int? target = targets.Take(kind, scope);
                if (target is not null)
                {
                    offers.AddRange(party.Claims.Select(claim => new IdRemap(claim.Mod.ChangeSet.ModId, kind, scope, claim.Added.Id, target.Value)));
                }
            }
        }

        List<IdRemap> remaps = pins
            .Select(pin => new IdRemap(pin.Key.ModId, pin.Key.Kind, pin.Key.Scope, pin.Key.FromId, pin.Value))
            .Concat(moves)
            .ToList();
        return new RemapPlan(Sorted(remaps), Sorted(moves), Sorted(offers), diagnostics);
    }

    /// <summary>
    /// GR-R12: a recorded entry stands while its mod is enabled and still adds the old id. Two entries for one old
    /// id cannot both stand; the one with the lowest target does, so the input order never decides. An entry whose
    /// target vanilla now uses (a game update filled a hole) stands no more: the old id is planned again, and when no
    /// other entry stands for it the plan carries <c>rules.remap_target_taken</c> (the owner, 2026-11-12).
    /// </summary>
    private static Dictionary<(string ModId, IdKind Kind, string Scope, int FromId), int> Pins(
        List<RemapMod> mods,
        Dictionary<string, IReadOnlyList<AddedId>> added,
        IReadOnlyList<IdRemap> recorded,
        BaseFileSet baseFiles,
        List<OperationDiagnostic> diagnostics)
    {
        var taken = new List<IdRemap>();
        var enabled = new HashSet<string>(mods.Where(mod => mod.Enabled).Select(mod => mod.ChangeSet.ModId), StringComparer.Ordinal);
        var pins = new Dictionary<(string ModId, IdKind Kind, string Scope, int FromId), int>();
        var targets = new HashSet<(string ModId, IdKind Kind, string Scope, int ToId)>();
        foreach (IdRemap remap in Sorted(recorded))
        {
            (string ModId, IdKind Kind, string Scope, int FromId) from = (remap.ModId, remap.Kind, remap.Scope, remap.FromId);
            if (remap.Kind is not (IdKind.MapEvent or IdKind.Map) || remap.ToId <= 0 || !enabled.Contains(remap.ModId) ||
                !AddedIdIndex.IsActive(remap, added[remap.ModId]) || pins.ContainsKey(from) ||
                !targets.Add((remap.ModId, remap.Kind, remap.Scope, remap.ToId)))
            {
                // Only a standing entry holds its target, so a losing duplicate leaves the next entry free.
                continue;
            }

            if (VanillaUses(remap.Kind, remap.Scope, remap.ToId, baseFiles))
            {
                taken.Add(remap);
                continue;
            }

            pins.Add(from, remap.ToId);
        }

        foreach (IdRemap remap in taken.Where(item => !pins.ContainsKey((item.ModId, item.Kind, item.Scope, item.FromId)))
                     .DistinctBy(item => (item.ModId, item.Kind, item.Scope, item.FromId)))
        {
            string where = remap.Kind == IdKind.MapEvent ? " in " + remap.Scope : "";
            diagnostics.Add(new OperationDiagnostic(
                DiagnosticCodes.RulesRemapTargetTaken,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Mod {remap.ModId}: {remap.Kind} {remap.FromId}{where} was recorded as moved to {remap.ToId}, which vanilla now uses, so the entry no longer applies and the id is planned again."),
                DiagnosticSeverity.Warning,
                remap.ModId));
        }

        return pins;
    }

    /// <summary>True when vanilla has a record at <paramref name="id"/>: a map file or tree record, or a map's event.</summary>
    private static bool VanillaUses(IdKind kind, string scope, int id, BaseFileSet baseFiles)
    {
        if (kind == IdKind.Map && baseFiles.Find(RpgDataSchema.MapKey(id)) is not null)
        {
            return true;
        }

        return VanillaList(kind, scope, baseFiles) is JArray list && id < list.Count && list[id].Type != JTokenType.Null;
    }

    /// <summary>Vanilla's list for a kind and scope, holes included: the map tree for maps, a map's events for events.</summary>
    private static JArray? VanillaList(IdKind kind, string scope, BaseFileSet baseFiles)
    {
        string file = kind == IdKind.Map ? RpgDataSchema.MapInfosKey : scope;
        BaseFile? baseFile = baseFiles.Find(file);
        if (baseFile is null || !ChangeSetBuilder.TryReadBase(baseFile, out JToken? root, out _))
        {
            return null;
        }

        return (kind == IdKind.Map ? root : (root as JObject)?[RpgDataSchema.EventsProperty]) as JArray;
    }

    /// <summary>GR-R3: claims with equal content are one party, because their ops fold into one.</summary>
    private static List<Party> Partition(IEnumerable<Claim> claims)
    {
        var parties = new List<Party>();
        foreach (Claim claim in claims)
        {
            Party? party = parties.FirstOrDefault(item => JToken.DeepEquals(item.Claims[0].Added.Content, claim.Added.Content));
            if (party is null)
            {
                parties.Add(new Party(claim));
            }
            else
            {
                party.Claims.Add(claim);
            }
        }

        return parties
            .OrderBy(party => party.Rank)
            .ThenBy(party => party.Claims.Min(claim => claim.Mod.ChangeSet.Priority))
            .ThenBy(party => party.Claims.Select(claim => claim.Mod.ChangeSet.ModId).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A map vanilla ships a file for, though its tree lacks the record: a mod that adds only the record edits that
    /// file in place, and a remap cannot rename vanilla's file.
    /// </summary>
    private static bool IsVanillaMapFile(IdKind kind, int id, BaseFileSet baseFiles)
    {
        return kind == IdKind.Map && baseFiles.Find(RpgDataSchema.MapKey(id)) is not null;
    }

    private static ModReferences ReferencesOf(RemapMod mod, Dictionary<string, ModReferences> cache)
    {
        if (!cache.TryGetValue(mod.ChangeSet.ModId, out ModReferences? found))
        {
            found = ReferenceMap.Scan(mod.ChangeSet, mod.PluginScripts);
            cache.Add(mod.ChangeSet.ModId, found);
        }

        return found;
    }

    private static List<IdRemap> Sorted(IEnumerable<IdRemap> remaps)
    {
        return remaps
            .OrderBy(remap => remap.ModId, StringComparer.Ordinal)
            .ThenBy(remap => remap.Kind)
            .ThenBy(remap => remap.Scope, StringComparer.Ordinal)
            .ThenBy(remap => remap.FromId)
            .ThenBy(remap => remap.ToId)
            .ToList();
    }

    /// <summary>One enabled mod's added id, at the id it has after its recorded entry.</summary>
    private sealed record Claim(RemapMod Mod, AddedId Added, int Effective, bool Pinned, bool Deployed);

    /// <summary>The mods of one clash that add equal content, so they keep or move together.</summary>
    private sealed class Party
    {
        public Party(Claim first)
        {
            Claims = [first];
        }

        public List<Claim> Claims { get; }

        /// <summary>GR-R4: a recorded entry, then a mod in <c>DeployedMods</c>, then the rest.</summary>
        public int Rank => Claims.Any(claim => claim.Pinned) ? 0 : Claims.Any(claim => claim.Deployed) ? 1 : 2;

        public bool CanMove { get; private set; }

        public bool IsUnchecked { get; private set; }

        /// <summary>
        /// A party moves only when no member is pinned, no member's id is fixed in vanilla's files and no member's
        /// change set names the id where a remap cannot follow it (GR-R9). A number found only in a shipped plugin
        /// file makes the move an offer (GR-R11).
        /// </summary>
        public void Classify(IdKind kind, Func<Claim, ModReferences> referencesOf, Func<Claim, bool> isFixed)
        {
            bool pinned = Claims.Any(claim => claim.Pinned);
            bool blocked = !pinned && Claims.Any(claim => isFixed(claim) || referencesOf(claim).Blocks(kind, claim.Added.Id));
            IsUnchecked = !pinned && !blocked && Claims.Any(claim => referencesOf(claim).IsUnchecked(claim.Added.Id));
            CanMove = !pinned && !blocked && !IsUnchecked;
        }
    }

    /// <summary>GR-R5: the next free id per kind and scope, above vanilla, every installed mod and every entry.</summary>
    private sealed class Targets
    {
        private readonly List<AddedId> _added;
        private readonly IReadOnlyList<IdRemap> _recorded;
        private readonly BaseFileSet _baseFiles;
        private readonly Dictionary<(IdKind Kind, string Scope), int?> _next = new();

        public Targets(List<AddedId> added, IReadOnlyList<IdRemap> recorded, BaseFileSet baseFiles)
        {
            _added = added;
            _recorded = recorded;
            _baseFiles = baseFiles;
        }

        public int? Take(IdKind kind, string scope)
        {
            if (!_next.TryGetValue((kind, scope), out int? next))
            {
                next = First(kind, scope);
            }

            // A vanilla map file outside the tree still owns its id.
            while (next is not null && kind == IdKind.Map && _baseFiles.Find(RpgDataSchema.MapKey(next.Value)) is not null)
            {
                next++;
            }

            int? limit = RpgDataSchema.RemapLimit(kind);
            if (next is null || limit is null || next > limit)
            {
                _next[(kind, scope)] = next;
                return null;
            }

            _next[(kind, scope)] = next + 1;
            return next;
        }

        private int? First(IdKind kind, string scope)
        {
            JArray? vanilla = VanillaList(kind, scope, _baseFiles);
            if (vanilla is null)
            {
                return null;
            }

            int used = Math.Max(0, vanilla.Count - 1);
            foreach (AddedId id in _added.Where(item => item.Kind == kind && item.Scope == scope))
            {
                used = Math.Max(used, id.Id);
            }

            foreach (IdRemap remap in _recorded.Where(item => item.Kind == kind && item.Scope == scope))
            {
                used = Math.Max(used, Math.Max(remap.FromId, remap.ToId));
            }

            return used + 1;
        }
    }
}
