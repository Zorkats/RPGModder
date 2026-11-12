using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Diagnostics;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// Composes the change sets of every enabled mod into one plan (guide 3.4) and applies the stored choices
/// (guide 3.5). The result does not depend on the order of the inputs (GR-C4); priority is read only by the two
/// plugin order defaults, which name profile order. Nothing is merged by text, pattern or load order (GR-C11).
/// </summary>
public static class Composer
{
    /// <param name="remapPlan">
    /// The plan, when lane A ran <see cref="IdRemapPlanner"/>: an <c>IdClash</c> one of its offers settles lists
    /// <see cref="ResolutionKind.Remap"/> and carries <c>rules.remap_unchecked</c> (GR-R11), and its diagnostics join
    /// the plan's.
    /// </param>
    public static MergePlan Compose(
        IReadOnlyList<ModChangeSet> changeSets,
        IReadOnlyList<Resolution> resolutions,
        BaseFileSet baseFiles,
        RemapPlan? remapPlan = null)
    {
        ArgumentNullException.ThrowIfNull(changeSets);
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentNullException.ThrowIfNull(baseFiles);

        List<ModChangeSet> ordered = changeSets.OrderBy(set => set.ModId, StringComparer.Ordinal).ToList();
        for (int index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].ModId == ordered[index - 1].ModId)
            {
                throw new ArgumentException($"Two change sets share the mod id {ordered[index].ModId}.", nameof(changeSets));
            }
        }

        var diagnostics = new List<OperationDiagnostic>();
        foreach (ModChangeSet set in ordered)
        {
            diagnostics.AddRange(set.Diagnostics);
        }

        diagnostics.AddRange(remapPlan?.Diagnostics ?? []);

        // The writer stamps versionId into System.json (C-7), so a broken one must stop the plan here, not there.
        BaseFile? system = baseFiles.Find(RpgDataSchema.SystemFileKey);
        if (system is not null && !ChangeSetBuilder.TryReadBase(system, out _, out OperationDiagnostic? systemProblem))
        {
            diagnostics.Add(systemProblem);
        }

        var priorities = ordered.ToDictionary(set => set.ModId, set => set.Priority, StringComparer.Ordinal);

        // One lookup for the whole plan: a large map repaint can give tens of thousands of collisions and choices.
        ILookup<string, Resolution> choices = resolutions.ToLookup(item => item.CollisionId, StringComparer.Ordinal);
        var ops = new List<MergedOp>();
        var collisions = new List<(Collision Collision, CollisionOutcome Outcome)>();
        IReadOnlyList<IdRemap> offers = remapPlan?.Offers ?? [];
        foreach (List<OpGroup> cluster in Cluster(Group(ordered)))
        {
            ComposeCluster(cluster, choices, priorities, offers, ops, collisions, diagnostics);
        }

        ComposePluginOrder(ops, choices, priorities, collisions, diagnostics);

        ops.Sort((left, right) => ChangeSetBuilder.CompareOps(left.Op, right.Op));
        collisions.Sort((left, right) => string.CompareOrdinal(left.Collision.Id, right.Collision.Id));

        List<OperationDiagnostic> allDiagnostics = diagnostics
            .Distinct()
            .OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Subject ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ThenBy(item => item.Severity)
            .ToList();

        List<CollisionOutcome> outcomes = collisions.Select(item => item.Outcome).ToList();
        bool deployable = allDiagnostics.All(item => item.Severity != DiagnosticSeverity.Error) &&
                          collisions.All(item => !item.Collision.Blocking || item.Outcome.Source != ResolutionSource.Unresolved);

        return new MergePlan(
            ops,
            collisions.Select(item => item.Collision).ToList(),
            outcomes,
            allDiagnostics,
            deployable,
            Hash(ops, outcomes));
    }

    /// <summary>GR-C1: ops on one key and path, with equal ops folded into one variant.</summary>
    private static List<OpGroup> Group(List<ModChangeSet> ordered)
    {
        var groups = new Dictionary<(string File, string Record, string Path), OpGroup>();
        foreach (ModChangeSet set in ordered)
        {
            foreach (ChangeOp op in set.Ops)
            {
                var key = (op.Key.File, op.Key.Record, op.Path);
                if (!groups.TryGetValue(key, out OpGroup? group))
                {
                    group = new OpGroup(op.Key, op.Path);
                    groups.Add(key, group);
                }

                group.Add(op, set.ModId);
            }
        }

        return groups.Values.ToList();
    }

    /// <summary>
    /// GR-C2: a group joins the outermost group whose place contains it: a whole-file value, else the shortest
    /// ancestor path in the same record. The head comes first in each cluster, the rest follow in place order.
    /// </summary>
    private static List<List<OpGroup>> Cluster(IReadOnlyList<OpGroup> groups)
    {
        var byPlace = new Dictionary<(string File, string Record, string Path), OpGroup>();
        foreach (OpGroup group in groups)
        {
            byPlace.Add((group.Key.File, group.Key.Record, group.Path), group);
        }

        var clusters = new List<List<OpGroup>>();
        var clusterOfHead = new Dictionary<OpGroup, List<OpGroup>>();
        IEnumerable<OpGroup> inPlaceOrder = groups
            .OrderBy(group => group.Key.File, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Record, StringComparer.Ordinal)
            .ThenBy(group => group.Path, StringComparer.Ordinal);
        foreach (OpGroup group in inPlaceOrder)
        {
            OpGroup head = HeadOf(group, byPlace);
            if (!clusterOfHead.TryGetValue(head, out List<OpGroup>? cluster))
            {
                cluster = [head];
                clusterOfHead.Add(head, cluster);
                clusters.Add(cluster);
            }

            if (!ReferenceEquals(group, head))
            {
                cluster.Add(group);
            }
        }

        return clusters;
    }

    private static OpGroup HeadOf(OpGroup group, Dictionary<(string File, string Record, string Path), OpGroup> byPlace)
    {
        if (byPlace.TryGetValue((group.Key.File, RecordKey.WholeFile, ""), out OpGroup? wholeFile))
        {
            return wholeFile;
        }

        foreach (string ancestor in MergePath.Ancestors(group.Path))
        {
            if (byPlace.TryGetValue((group.Key.File, group.Key.Record, ancestor), out OpGroup? found))
            {
                return found;
            }
        }

        return group;
    }

    private static void ComposeCluster(
        List<OpGroup> cluster,
        ILookup<string, Resolution> choices,
        Dictionary<string, int> priorities,
        IReadOnlyList<IdRemap> offers,
        List<MergedOp> ops,
        List<(Collision, CollisionOutcome)> collisions,
        List<OperationDiagnostic> diagnostics)
    {
        List<string> mods = cluster
            .SelectMany(group => group.Variants)
            .SelectMany(variant => variant.ModIds)
            .Distinct()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        bool clash = cluster.Count == 1 ? cluster[0].Variants.Count > 1 : mods.Count > 1;
        if (!clash)
        {
            foreach (OpGroup group in cluster)
            {
                ops.AddRange(group.Variants.Select(variant => variant.ToMergedOp()));
            }

            return;
        }

        Collision collision = OfferRemaps(MakeCollision(cluster, mods), offers, diagnostics);
        CollisionOutcome outcome = Decide(collision, choices, diagnostics);
        collisions.Add((collision, outcome));

        ResolutionKind? kind = outcome.Source == ResolutionSource.Unresolved ? null : outcome.Kind;
        switch (kind)
        {
            case null:
            case ResolutionKind.PickVanilla:
                // Unresolved: the plan is not deployable and the clashing ops are described by the collision.
                break;
            case ResolutionKind.PickMod when cluster.Count > 1 && !IsHeadMod(cluster[0], outcome.ModId!):
                // Choosing a change inside the head keeps every inner change: only the head is dropped, and the
                // inner ops are composed again, so a clash among them becomes a collision of its own (seam 11).
                foreach (List<OpGroup> inner in Cluster(cluster.Skip(1).ToList()))
                {
                    ComposeCluster(inner, choices, priorities, offers, ops, collisions, diagnostics);
                }

                break;
            case ResolutionKind.KeepBothInProfileOrder when collision.Kind == CollisionKind.PluginOrderClash:
                // The anchors apply in profile order, so the last one wins (seam 11.1). An OrderAmbiguous
                // collision is composed after the clusters, by ComposePluginOrder.
                ops.Add(LatestInProfileOrder(cluster[0], priorities).ToMergedOp());
                break;
            case ResolutionKind.PickMod:
                foreach (OpGroup group in cluster)
                {
                    ops.AddRange(group.Variants
                        .Where(variant => variant.ModIds.Contains(outcome.ModId!, StringComparer.Ordinal))
                        .Select(variant => variant.ToMergedOp()));
                }

                break;
            default:
                // Decide accepts only kinds in Allowed, and no cluster kind of 2a allows another choice.
                throw new UnreachableException($"Resolution {kind} is not applied to {collision.Kind}.");
        }
    }

    /// <summary>The variant of the mod latest in profile order; equal priorities fall back to the mod id that sorts last.</summary>
    private static Variant LatestInProfileOrder(OpGroup group, Dictionary<string, int> priorities)
    {
        return group.Variants
            .OrderByDescending(variant => variant.ModIds.Max(id => priorities[id]))
            .ThenByDescending(variant => variant.ModIds.Max(StringComparer.Ordinal), StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// GR-C10: entries from more than one mod behind one anchor are an <c>OrderAmbiguous</c> collision (seam 11.1),
    /// settled here, after every cluster, because the anchors are only final once each <c>PluginOrderClash</c> is.
    /// The chosen order is written back as a chain, so each <c>@after</c> names the entry placed right before it.
    /// A <c>null</c> anchor sends a new entry to the end and a moved entry to the front, so each forms its own group:
    /// <c>@after</c> on <c>*</c> for the end, <c>@first</c> on <c>*</c> for the front.
    /// </summary>
    private static void ComposePluginOrder(
        List<MergedOp> ops,
        ILookup<string, Resolution> choices,
        Dictionary<string, int> priorities,
        List<(Collision, CollisionOutcome)> collisions,
        List<OperationDiagnostic> diagnostics)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (MergedOp merged in ops)
        {
            ChangeOp op = merged.Op;
            if (op.Key.File == RpgDataSchema.PluginListKey && op.Kind == ChangeKind.AddRecord && !op.Key.IsWholeFile)
            {
                added.Add(op.Key.Record);
            }
        }

        var byAnchor = new SortedDictionary<(string Record, string Path), List<int>>(Comparer<(string Record, string Path)>.Create(
            (left, right) =>
            {
                int byRecord = string.CompareOrdinal(left.Record, right.Record);
                return byRecord != 0 ? byRecord : string.CompareOrdinal(left.Path, right.Path);
            }));
        var afterOps = new List<int>();
        for (int index = 0; index < ops.Count; index++)
        {
            ChangeOp op = ops[index].Op;
            if (op.Key.File != RpgDataSchema.PluginListKey || op.Path != MergePath.AfterLeaf)
            {
                continue;
            }

            afterOps.Add(index);

            string? anchor = op.Value?.Type == JTokenType.String ? op.Value.Value<string>() : null;
            (string Record, string Path) place = anchor is not null
                ? (RecordKey.ForPlugin(anchor), MergePath.AfterLeaf)
                : (RecordKey.WholeFile, added.Contains(op.Key.Record) ? MergePath.AfterLeaf : MergePath.FirstLeaf);
            if (!byAnchor.TryGetValue(place, out List<int>? entries))
            {
                entries = new List<int>();
                byAnchor.Add(place, entries);
            }

            entries.Add(index);
        }

        foreach (((string anchorRecord, string path), List<int> entries) in byAnchor)
        {
            List<string> mods = entries
                .SelectMany(index => ops[index].ModIds)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (entries.Count < 2 || mods.Count < 2)
            {
                continue;
            }

            var key = new RecordKey(RpgDataSchema.PluginListKey, anchorRecord);
            string id = CollisionId(CollisionKind.OrderAmbiguous, key, path, mods);
            var collision = new Collision(
                id,
                CollisionKind.OrderAmbiguous,
                key,
                path,
                mods,
                IsBlocking(CollisionKind.OrderAmbiguous),
                AllowedFor(CollisionKind.OrderAmbiguous),
                OrderFingerprint(ops, entries));
            CollisionOutcome outcome = Decide(collision, choices, diagnostics);
            collisions.Add((collision, outcome));

            string? first = outcome.Kind == ResolutionKind.PickMod ? outcome.ModId : null;
            List<int> placed = entries
                .OrderBy(index => first is not null && ops[index].ModIds.Contains(first, StringComparer.Ordinal) ? 0 : 1)
                .ThenBy(index => ops[index].ModIds.Min(mod => priorities[mod]))
                .ThenBy(index => ops[index].Op.Key.Record, StringComparer.Ordinal)
                .ToList();

            for (int position = 1; position < placed.Count; position++)
            {
                MergedOp merged = ops[placed[position]];
                string before = RunTail(ops, afterOps, ops[placed[position - 1]]);
                ops[placed[position]] = merged with { Op = merged.Op with { Value = new JValue(before) } };
            }
        }
    }

    /// <summary>
    /// The last entry of the run that starts at <paramref name="start"/>: each next entry is anchored on the one
    /// before and comes from the same mods, so a mod's own run of plugins is never split by another mod's.
    /// </summary>
    private static string RunTail(List<MergedOp> ops, List<int> afterOps, MergedOp start)
    {
        RecordKey.TryGetPlugin(start.Op.Key.Record, out string tail);
        var seen = new HashSet<string>(StringComparer.Ordinal) { tail };
        while (true)
        {
            MergedOp? next = afterOps.Select(index => ops[index]).FirstOrDefault(merged =>
                merged.Op.Value?.Type == JTokenType.String &&
                merged.Op.Value.Value<string>() == tail &&
                merged.ModIds.SequenceEqual(start.ModIds, StringComparer.Ordinal));
            if (next is null || !RecordKey.TryGetPlugin(next.Op.Key.Record, out string name) || !seen.Add(name))
            {
                return tail;
            }

            tail = name;
        }
    }

    /// <summary>GR-X2 for an order collision: the entries behind the anchor and their mods, by record.</summary>
    private static string OrderFingerprint(List<MergedOp> ops, List<int> entries)
    {
        var items = new JArray();
        foreach (MergedOp merged in entries.Select(index => ops[index]).OrderBy(item => item.Op.Key.Record, StringComparer.Ordinal))
        {
            items.Add(new JArray(merged.Op.Key.Record, new JArray(merged.ModIds.ToArray<object>())));
        }

        return MergeJson.Sha256Hex(MergeJson.Write(items));
    }

    /// <summary>
    /// GR-R11: an <c>IdClash</c> an offer settles lists <c>Remap</c>, stays blocking and has no default, and each
    /// offer carries the warning once, though a map offer settles two collisions (its file and its tree record).
    /// Lane A carries a chosen <c>Remap</c> out by recording the offer (seam 4.3).
    /// </summary>
    private static Collision OfferRemaps(Collision collision, IReadOnlyList<IdRemap> offers, List<OperationDiagnostic> diagnostics)
    {
        List<IdRemap> settling = offers.Where(offer => RemapPlaces.Settles(offer, collision)).ToList();
        if (settling.Count == 0)
        {
            return collision;
        }

        foreach (IdRemap offer in settling)
        {
            diagnostics.Add(new OperationDiagnostic(
                DiagnosticCodes.RulesRemapUnchecked,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Mod {offer.ModId}: {offer.Kind} {offer.FromId}{(offer.Kind == IdKind.MapEvent ? " in " + offer.Scope : "")} can move to {offer.ToId} to settle its id clash, but a plugin file the mod ships contains that number."),
                DiagnosticSeverity.Warning,
                offer.ModId));
        }

        // Declaration order, so Remap sits between the choices the collision already allows.
        return collision with { Allowed = collision.Allowed.Append(ResolutionKind.Remap).Distinct().Order().ToList() };
    }

    private static bool IsHeadMod(OpGroup head, string modId)
    {
        return head.Variants.Any(variant => variant.ModIds.Contains(modId, StringComparer.Ordinal));
    }

    private static Collision MakeCollision(List<OpGroup> cluster, List<string> mods)
    {
        OpGroup head = cluster[0];
        List<ChangeOp> all = cluster.SelectMany(group => group.Variants).Select(variant => variant.Op).ToList();

        CollisionKind kind;
        if (all.All(op => op.Kind == ChangeKind.ReplaceFile))
        {
            kind = CollisionKind.FileClash;
        }
        else if (all.Any(op => op.Kind == ChangeKind.RemoveRecord))
        {
            kind = CollisionKind.DeleteVersusEdit;
        }
        else if (all.All(op => op.Kind == ChangeKind.AddRecord))
        {
            kind = CollisionKind.IdClash;
        }
        else if (cluster.Count == 1 && head.Path == MergePath.AfterLeaf)
        {
            kind = CollisionKind.PluginOrderClash;
        }
        else if (RpgDataSchema.IsPageSetting(head.Key, head.Path))
        {
            kind = CollisionKind.PageClash;
        }
        else if (cluster.Count == 1 && all.All(op => op.Kind == ChangeKind.EditSequence))
        {
            kind = CollisionKind.SequenceClash;
        }
        else if (cluster.Count == 1 && all.All(op => op.Kind == ChangeKind.EditCommands))
        {
            kind = CollisionKind.LogicClash;
        }
        else
        {
            kind = CollisionKind.ValueClash;
        }

        // A mod with two variants of one place clashes with itself: a recorded remap moved an id onto one the mod now
        // adds as well. Picking that mod would keep both ops, so it is not a choice (seam 11.2).
        IReadOnlyList<ResolutionKind> allowed = AllowedFor(kind);
        if (cluster.Any(group => group.Variants.SelectMany(variant => variant.ModIds).GroupBy(modId => modId, StringComparer.Ordinal).Any(ids => ids.Count() > 1)))
        {
            allowed = allowed.Where(item => item != ResolutionKind.PickMod).ToList();
        }

        string id = CollisionId(kind, head.Key, head.Path, mods);
        return new Collision(id, kind, head.Key, head.Path, mods, IsBlocking(kind), allowed, Fingerprint(cluster));
    }

    /// <summary>
    /// GR-X1: the first 16 hex characters of SHA-256 over the kind, file, record, path and sorted mod ids, one per
    /// line. No value takes part, so a mod update keeps the player's choice.
    /// </summary>
    internal static string CollisionId(CollisionKind kind, RecordKey key, string path, IReadOnlyList<string> sortedModIds)
    {
        var lines = new List<string> { kind.ToString(), key.File, key.Record, path };
        lines.AddRange(sortedModIds);
        return MergeJson.Sha256Hex(string.Join('\n', lines))[..16];
    }

    /// <summary>GR-X2: a hash of every colliding value, by place and by mod.</summary>
    private static string Fingerprint(List<OpGroup> cluster)
    {
        var entries = new JArray();
        foreach (OpGroup group in cluster.OrderBy(item => item.Key.Record, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal))
        {
            foreach (Variant variant in group.Variants)
            {
                entries.Add(new JArray(
                    group.Key.Record,
                    group.Path,
                    variant.Op.Kind.ToString(),
                    variant.Op.Value is null ? new JArray() : new JArray(variant.Op.Value.DeepClone()),
                    new JArray(variant.ModIds.ToArray<object>())));
            }
        }

        return MergeJson.Sha256Hex(MergeJson.Write(entries));
    }

    private static CollisionOutcome Decide(Collision collision, ILookup<string, Resolution> choices, List<OperationDiagnostic> diagnostics)
    {
        Resolution? stored = choices[collision.Id]
            .Where(item => IsApplicable(item, collision))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.ModId ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Fingerprint ?? "", StringComparer.Ordinal)
            .FirstOrDefault();

        if (stored is not null)
        {
            string? modId = stored.Kind == ResolutionKind.PickMod ? stored.ModId : null;

            // A choice stored without a fingerprint cannot be checked against the content, so it is taken as made.
            if (stored.Fingerprint is null || stored.Fingerprint == collision.Fingerprint)
            {
                return new CollisionOutcome(collision.Id, ResolutionSource.Profile, stored.Kind, modId);
            }

            diagnostics.Add(new OperationDiagnostic(
                DiagnosticCodes.RulesResolutionReused,
                $"A stored choice for collision {collision.Id} was applied to content that changed since it was made.",
                DiagnosticSeverity.Info,
                collision.Id));
            return new CollisionOutcome(collision.Id, ResolutionSource.ProfileReused, stored.Kind, modId);
        }

        ResolutionKind? fallback = DefaultFor(collision.Kind);
        return fallback is null
            ? new CollisionOutcome(collision.Id, ResolutionSource.Unresolved, null, null)
            : new CollisionOutcome(collision.Id, ResolutionSource.Default, fallback, null);
    }

    /// <summary>
    /// A stored choice applies when its kind is allowed for the collision and Compose carries it out (seam 4.3),
    /// and a <c>PickMod</c> names one of the collision's mods. Anything else leaves the collision unresolved, so the
    /// player chooses again.
    /// </summary>
    private static bool IsApplicable(Resolution resolution, Collision collision)
    {
        if (resolution.Kind is ResolutionKind.DisableMod or ResolutionKind.Remap ||
            !collision.Allowed.Contains(resolution.Kind))
        {
            return false;
        }

        return resolution.Kind != ResolutionKind.PickMod ||
               (resolution.ModId is not null && collision.ModIds.Contains(resolution.ModId, StringComparer.Ordinal));
    }

    /// <summary>The "Blocks deploy until resolved" column of guide 3.5, for slice 2a.</summary>
    private static bool IsBlocking(CollisionKind kind)
    {
        return kind switch
        {
            CollisionKind.ValueClash or CollisionKind.DeleteVersusEdit or CollisionKind.IdClash or CollisionKind.SequenceClash
                or CollisionKind.LogicClash or CollisionKind.PageClash or CollisionKind.FileClash
                or CollisionKind.BaseMismatch => true,
            CollisionKind.PluginOrderClash or CollisionKind.OrderAmbiguous
                or CollisionKind.SwitchBandConflict or CollisionKind.StateIdOverlap => false,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A collision kind guide 3.5 does not list.")
        };
    }

    /// <summary>
    /// The "Allowed resolutions" column of guide 3.5 for slice 2a, in declaration order. <c>Remap</c> is added by
    /// <see cref="OfferRemaps"/> (2r) and <c>Gate</c> comes with 2c; a <c>SwitchBandConflict</c> is settled by moving the band, never by a choice.
    /// </summary>
    private static IReadOnlyList<ResolutionKind> AllowedFor(CollisionKind kind)
    {
        return kind switch
        {
            CollisionKind.ValueClash or CollisionKind.DeleteVersusEdit or CollisionKind.SequenceClash
                or CollisionKind.LogicClash or CollisionKind.PageClash => [ResolutionKind.PickMod, ResolutionKind.PickVanilla],
            CollisionKind.IdClash => [ResolutionKind.PickMod, ResolutionKind.DisableMod],
            CollisionKind.FileClash => [ResolutionKind.PickMod],
            CollisionKind.PluginOrderClash or CollisionKind.OrderAmbiguous => [ResolutionKind.PickMod, ResolutionKind.KeepBothInProfileOrder],
            CollisionKind.BaseMismatch => [ResolutionKind.Accept, ResolutionKind.DisableMod],
            CollisionKind.SwitchBandConflict => [],
            CollisionKind.StateIdOverlap => [ResolutionKind.Accept],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A collision kind guide 3.5 does not list.")
        };
    }

    /// <summary>
    /// The "Default" column of guide 3.5 for slice 2a. Only the order kinds and <c>StateIdOverlap</c> have one; a
    /// <c>BaseMismatch</c> waits for the player to accept that mod (owner decision, 2026-11-12).
    /// </summary>
    private static ResolutionKind? DefaultFor(CollisionKind kind)
    {
        return kind switch
        {
            CollisionKind.PluginOrderClash or CollisionKind.OrderAmbiguous => ResolutionKind.KeepBothInProfileOrder,
            CollisionKind.StateIdOverlap => ResolutionKind.Accept,
            _ => null
        };
    }

    /// <summary>Lane B's half of GR-C9 and TST-5: SHA-256 over a canonical form of the ops and the outcomes.</summary>
    private static string Hash(List<MergedOp> ops, List<CollisionOutcome> outcomes)
    {
        var opEntries = new JArray();
        foreach (MergedOp merged in ops)
        {
            ChangeOp op = merged.Op;
            opEntries.Add(new JArray(
                op.Key.File,
                op.Key.Record,
                op.Path,
                op.Kind.ToString(),
                op.Value is null ? new JArray() : new JArray(op.Value.DeepClone()),
                Text(op.SourceFile),
                new JArray(merged.ModIds.ToArray<object>())));
        }

        var outcomeEntries = new JArray();
        foreach (CollisionOutcome outcome in outcomes)
        {
            outcomeEntries.Add(new JArray(
                outcome.CollisionId,
                outcome.Source.ToString(),
                Text(outcome.Kind?.ToString()),
                Text(outcome.ModId)));
        }

        var root = new JArray(RpgDataSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture), opEntries, outcomeEntries);
        return MergeJson.Sha256Hex(MergeJson.Write(root));
    }

    private static JValue Text(string? value)
    {
        return value is null ? JValue.CreateNull() : new JValue(value);
    }

    private sealed class OpGroup
    {
        public OpGroup(RecordKey key, string path)
        {
            Key = key;
            Path = path;
        }

        public RecordKey Key { get; }

        public string Path { get; }

        public List<Variant> Variants { get; } = new();

        /// <summary>Ops arrive in mod id order, so each variant's op is the one of the mod whose id sorts first.</summary>
        public void Add(ChangeOp op, string modId)
        {
            foreach (Variant variant in Variants)
            {
                if (variant.Op.Kind == op.Kind && ValuesEqual(variant.Op.Value, op.Value))
                {
                    variant.ModIds.Add(modId);
                    return;
                }
            }

            Variants.Add(new Variant(op, [modId]));
        }

        private static bool ValuesEqual(JToken? left, JToken? right)
        {
            return left is null ? right is null : right is not null && JToken.DeepEquals(left, right);
        }
    }

    private sealed record Variant(ChangeOp Op, List<string> ModIds)
    {
        public MergedOp ToMergedOp()
        {
            return new MergedOp(Op, ModIds.ToList());
        }
    }
}
