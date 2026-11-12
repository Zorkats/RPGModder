using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

/// <summary>
/// Renders a deployable plan into the files a deploy writes. Only touched files are listed; every other file
/// stays vanilla and is copied by lane A (GR-M6). Every touched data file goes through one serializer (GR-M7);
/// a whole file a mod supplies unchanged is listed as a copy of the mod's file.
/// </summary>
public static class MergedFileWriter
{
    public static IReadOnlyList<MergedFile> Write(MergePlan plan, BaseFileSet baseFiles, int? systemVersionId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseFiles);

        if (!plan.IsDeployable)
        {
            throw new InvalidOperationException("Only a deployable plan can be written.");
        }

        var files = new SortedDictionary<string, List<MergedOp>>(StringComparer.Ordinal);
        foreach (MergedOp merged in plan.Ops)
        {
            if (!files.TryGetValue(merged.Op.Key.File, out List<MergedOp>? fileOps))
            {
                fileOps = new List<MergedOp>();
                files.Add(merged.Op.Key.File, fileOps);
            }

            fileOps.Add(merged);
        }

        if (systemVersionId is not null)
        {
            if (baseFiles.Find(RpgDataSchema.SystemFileKey) is null)
            {
                throw new ArgumentException("A versionId needs data/System.json among the base files.", nameof(baseFiles));
            }

            files.TryAdd(RpgDataSchema.SystemFileKey, new List<MergedOp>());
        }

        var result = new List<MergedFile>();
        foreach ((string file, List<MergedOp> fileOps) in files)
        {
            List<string> modIds = fileOps
                .SelectMany(merged => merged.ModIds)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            result.Add(RpgDataSchema.KindOf(file) switch
            {
                MergeFileKind.Opaque => Copy(file, fileOps, modIds),
                MergeFileKind.PluginList => new MergedFile(file, MergedFileSource.Text, RenderPluginList(file, fileOps, baseFiles), null, null, modIds),
                MergeFileKind.ConfigSave => new MergedFile(file, MergedFileSource.Text, RenderConfigSave(file, fileOps, baseFiles), null, null, modIds),
                _ => WriteDataFile(file, fileOps, baseFiles, systemVersionId, modIds)
            });
        }

        return result;
    }

    private static MergedFile WriteDataFile(string file, List<MergedOp> fileOps, BaseFileSet baseFiles, int? systemVersionId, List<string> modIds)
    {
        BaseFile? baseFile = baseFiles.Find(file);
        if (baseFile is null)
        {
            // GR-M9: a new file is one AddRecord on *, copied as the mod shipped it unless a patch changed it.
            ChangeOp added = SingleOp(fileOps, ChangeKind.AddRecord);
            return added.SourceFile is null
                ? new MergedFile(file, MergedFileSource.Text, MergeJson.Write(Value(added)), null, null, modIds)
                : new MergedFile(file, MergedFileSource.ModFile, null, added.ModId, added.SourceFile, modIds);
        }

        JToken root = Render(file, baseFile, fileOps);
        if (file == RpgDataSchema.SystemFileKey && systemVersionId is not null)
        {
            SetVersionId(root, systemVersionId.Value);
        }

        return new MergedFile(file, MergedFileSource.Text, MergeJson.Write(root), null, null, modIds);
    }

    /// <summary>A replaced opaque file is the bytes of the mod whose id sorts first among those that ship them.</summary>
    private static MergedFile Copy(string file, List<MergedOp> fileOps, List<string> modIds)
    {
        ChangeOp replaced = SingleOp(fileOps, ChangeKind.ReplaceFile);
        return new MergedFile(
            file,
            MergedFileSource.ModFile,
            null,
            replaced.ModId,
            replaced.SourceFile ?? throw new InvalidOperationException($"The ReplaceFile op for {file} names no source file."),
            modIds);
    }

    private static ChangeOp SingleOp(List<MergedOp> fileOps, ChangeKind kind)
    {
        if (fileOps.Count != 1 || fileOps[0].Op.Kind != kind || !fileOps[0].Op.Key.IsWholeFile || fileOps[0].Op.Path.Length != 0)
        {
            throw new InvalidOperationException($"{fileOps[0].Op.Key.File} needs exactly one whole-file {kind} op.");
        }

        return fileOps[0].Op;
    }

    private static JToken Render(string file, BaseFile baseFile, List<MergedOp> fileOps)
    {
        JToken root = MergeJson.Parse(baseFile.Text
                                      ?? throw new ArgumentException($"The base file {file} has no text.", nameof(baseFile)));

        var removals = new List<ChangeOp>();
        foreach (MergedOp merged in fileOps)
        {
            ChangeOp op = merged.Op;
            if (op.Kind is ChangeKind.RemoveLeaf or ChangeKind.RemoveRecord)
            {
                removals.Add(op);
            }
            else if (op.Key.IsWholeFile && op.Path.Length == 0)
            {
                root = Value(op);
            }
            else
            {
                Set(root, op);
            }
        }

        // Removals run last and from the highest index down, so an index still names the element it named in vanilla.
        removals.Sort(CompareDescending);
        var removedRecords = new Dictionary<JArray, HashSet<int>>(ReferenceEqualityComparer.Instance);
        foreach (ChangeOp op in removals)
        {
            if (op.Kind == ChangeKind.RemoveRecord)
            {
                RemoveRecord(root, op, removedRecords);
            }
            else
            {
                RemoveLeaf(RecordOf(root, op.Key), op);
            }
        }

        // Removed records at the end of their list are cut off, as the editor does: database files and map events.
        foreach ((JArray records, HashSet<int> removed) in removedRecords)
        {
            while (records.Count > 0 && removed.Contains(records.Count - 1))
            {
                records.RemoveAt(records.Count - 1);
            }
        }

        return root;
    }

    /// <summary>
    /// Vanilla's list with the ops applied: leaf edits in place, removals, new entries, then every entry with an
    /// <c>@after</c> placed by <see cref="PluginPlacement"/>. Prefix and suffix are vanilla's (seam 11.1).
    /// </summary>
    private static string RenderPluginList(string file, List<MergedOp> fileOps, BaseFileSet baseFiles)
    {
        PluginListText? vanilla = PluginListText.Empty();
        BaseFile? baseFile = baseFiles.Find(file);
        if (baseFile is not null && !PluginListText.TryParse(baseFile.Text, out vanilla, out string? error))
        {
            throw new ArgumentException($"The base file {file} cannot be read: {error}.", nameof(baseFiles));
        }

        if (fileOps.FirstOrDefault(merged => merged.Op.Key.IsWholeFile) is { } whole)
        {
            // A whole list clusters with every other op of the file, so nothing else survives beside it.
            return (vanilla with { Entries = Value(whole.Op) as JArray ?? throw Misfit(whole.Op) }).Render();
        }

        var order = new List<string>();
        var entries = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (JToken element in vanilla.Entries)
        {
            string name = (element is JObject entry ? PluginListDiff.NameOf(entry) : null)
                          ?? throw new InvalidOperationException($"{file} has an entry without a name, so only whole-list ops fit it.");
            if (!entries.TryAdd(name, (JObject)element.DeepClone()))
            {
                throw new InvalidOperationException($"{file} names {name} twice, so only whole-list ops fit it.");
            }

            order.Add(name);
        }

        var added = new HashSet<string>(StringComparer.Ordinal);
        var anchors = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (MergedOp merged in fileOps)
        {
            ChangeOp op = merged.Op;
            if (!RecordKey.TryGetPlugin(op.Key.Record, out string name))
            {
                throw Misfit(op);
            }

            if (op.Kind == ChangeKind.AddRecord)
            {
                entries[name] = Value(op) as JObject ?? throw Misfit(op);
                added.Add(name);
            }
            else if (op.Kind == ChangeKind.RemoveRecord)
            {
                entries.Remove(name);
            }
            else if (op.Path == MergePath.AfterLeaf)
            {
                anchors[name] = Value(op).Type == JTokenType.String ? Value(op).Value<string>() : null;
            }
            else if (!entries.TryGetValue(name, out JObject? entry))
            {
                throw Misfit(op);
            }
            else if (op.Kind == ChangeKind.SetLeaf)
            {
                IReadOnlyList<PathSegment> segments = MergePath.Parse(op.Path);
                Put(Find(entry, segments, op), segments[^1], Value(op));
            }
            else if (op.Kind == ChangeKind.RemoveLeaf)
            {
                RemoveLeaf(entry, op);
            }
            else
            {
                throw Misfit(op);
            }
        }

        // An anchor for an entry that is gone (removed by one mod, moved by another) has nothing to place.
        var present = new HashSet<string>(entries.Keys, StringComparer.Ordinal);
        Dictionary<string, string?> live = anchors.Where(pair => present.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
        var list = new JArray();
        foreach (string name in PluginPlacement.Place(order, present, added, live))
        {
            list.Add(entries[name]);
        }

        return (vanilla with { Entries = list }).Render();
    }

    /// <summary>GR-M10: the live save (or <c>{}</c>) with each top-level value set, encoded as the game reads it.</summary>
    private static string RenderConfigSave(string file, List<MergedOp> fileOps, BaseFileSet baseFiles)
    {
        if (!ChangeSetBuilder.TryReadConfigBase(baseFiles.Find(file), file, out JObject? live, out OperationDiagnostic? problem))
        {
            throw new ArgumentException(problem.Message, nameof(baseFiles));
        }

        foreach (MergedOp merged in fileOps)
        {
            if (merged.Op.Kind != ChangeKind.SetLeaf || !merged.Op.Key.IsWholeFile)
            {
                throw Misfit(merged.Op);
            }

            Set(live, merged.Op);
        }

        return ConfigSaveText.Encode(file, live);
    }

    private static void Set(JToken root, ChangeOp op)
    {
        IReadOnlyList<PathSegment> segments = MergePath.Parse(op.Path);
        if (segments.Count == 0)
        {
            // A whole record: AddRecord, or a record that is not an object.
            (JArray records, int index) = SlotOf(root, op.Key);
            Put(records, PathSegment.At(index), Value(op));
            return;
        }

        JToken container = Find(RecordOf(root, op.Key), segments, op);
        Put(container, segments[^1], Value(op));
    }

    private static void RemoveRecord(JToken root, ChangeOp op, Dictionary<JArray, HashSet<int>> removedRecords)
    {
        _ = RecordOf(root, op.Key);
        (JArray records, int index) = SlotOf(root, op.Key);
        records[index] = JValue.CreateNull();
        if (!removedRecords.TryGetValue(records, out HashSet<int>? removed))
        {
            removed = new HashSet<int>();
            removedRecords.Add(records, removed);
        }

        removed.Add(index);
    }

    private static void RemoveLeaf(JToken record, ChangeOp op)
    {
        IReadOnlyList<PathSegment> segments = MergePath.Parse(op.Path);
        if (segments.Count == 0)
        {
            throw Misfit(op);
        }

        JToken container = Find(record, segments, op);
        PathSegment last = segments[^1];
        if (container is JObject parent && last.Name is not null && parent.Remove(last.Name))
        {
            return;
        }

        if (container is JArray array && last.Name is null && last.Index < array.Count)
        {
            // Cutting the tail keeps the list short, as the mod left it; a hole in the middle keeps later indexes.
            if (last.Index == array.Count - 1)
            {
                array.RemoveAt(last.Index);
            }
            else
            {
                array[last.Index] = JValue.CreateNull();
            }

            return;
        }

        throw Misfit(op);
    }

    /// <summary>The container of the last segment. Every step must exist: the walker only names places vanilla has.</summary>
    private static JToken Find(JToken record, IReadOnlyList<PathSegment> segments, ChangeOp op)
    {
        JToken container = record;
        for (int index = 0; index < segments.Count - 1; index++)
        {
            container = Get(container, segments[index]) ?? throw Misfit(op);
        }

        return container;
    }

    private static void SetVersionId(JToken root, int versionId)
    {
        if (root is not JObject system)
        {
            throw new InvalidOperationException("data/System.json has no object root to carry a versionId.");
        }

        Put(system, PathSegment.Property(RpgDataSchema.VersionIdField), new JValue(versionId));
    }

    private static JToken RecordOf(JToken root, RecordKey key)
    {
        if (key.IsWholeFile)
        {
            return root;
        }

        (JArray records, int index) = SlotOf(root, key);
        return index < records.Count && records[index].Type != JTokenType.Null
            ? records[index]
            : throw new InvalidOperationException($"{key.File} has no record {key.Record}.");
    }

    /// <summary>The list that holds a record and its index there: the root for <c>id:</c>, a map's <c>events</c> for <c>event:</c>.</summary>
    private static (JArray Records, int Index) SlotOf(JToken root, RecordKey key)
    {
        if (RecordKey.TryGetIndex(key.Record, out int index))
        {
            return (root as JArray ?? throw new InvalidOperationException($"A record op on {key.File} needs a file whose root is an array."), index);
        }

        if (RecordKey.TryGetEvent(key.Record, out index))
        {
            return (root[RpgDataSchema.EventsProperty] as JArray
                    ?? throw new InvalidOperationException($"An event op on {key.File} needs a map with an events list."), index);
        }

        throw new InvalidOperationException($"{key.Record} is not a record of {key.File}.");
    }

    private static InvalidOperationException Misfit(ChangeOp op)
    {
        return new InvalidOperationException($"The {op.Kind} op at {op.Key.File} {op.Key.Record} {op.Path} does not fit the file.");
    }

    private static JToken? Get(JToken container, PathSegment segment)
    {
        if (segment.Name is not null)
        {
            return container is JObject parent ? parent.Property(segment.Name, StringComparison.Ordinal)?.Value : null;
        }

        return container is JArray array && segment.Index < array.Count ? array[segment.Index] : null;
    }

    /// <summary>An existing property keeps its place; a new one goes at the end (GR-M7). An index past the end pads with <c>null</c>.</summary>
    private static void Put(JToken container, PathSegment segment, JToken value)
    {
        if (segment.Name is not null && container is JObject parent)
        {
            JProperty? existing = parent.Property(segment.Name, StringComparison.Ordinal);
            if (existing is null)
            {
                parent.Add(segment.Name, value);
            }
            else
            {
                existing.Value = value;
            }

            return;
        }

        if (segment.Name is null && container is JArray array)
        {
            while (array.Count <= segment.Index)
            {
                array.Add(JValue.CreateNull());
            }

            array[segment.Index] = value;
            return;
        }

        throw new InvalidOperationException("A merged value does not fit the shape of its file.");
    }

    /// <summary>A copy of the op's value. A JSON <c>null</c> is a value; only a missing one (the remove kinds) throws.</summary>
    private static JToken Value(ChangeOp op)
    {
        return op.Value?.DeepClone() ?? throw new InvalidOperationException($"A {op.Kind} op carries no value.");
    }

    private static int CompareDescending(ChangeOp left, ChangeOp right)
    {
        int result = CompareRecords(right.Key.Record, left.Key.Record);
        if (result != 0)
        {
            return result;
        }

        IReadOnlyList<PathSegment> leftSegments = MergePath.Parse(left.Path);
        IReadOnlyList<PathSegment> rightSegments = MergePath.Parse(right.Path);
        for (int index = 0; index < Math.Min(leftSegments.Count, rightSegments.Count); index++)
        {
            result = CompareSegments(rightSegments[index], leftSegments[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return rightSegments.Count.CompareTo(leftSegments.Count);
    }

    private static int CompareRecords(string left, string right)
    {
        if ((RecordKey.TryGetIndex(left, out int leftIndex) && RecordKey.TryGetIndex(right, out int rightIndex)) ||
            (RecordKey.TryGetEvent(left, out leftIndex) && RecordKey.TryGetEvent(right, out rightIndex)))
        {
            return leftIndex.CompareTo(rightIndex);
        }

        return string.CompareOrdinal(left, right);
    }

    private static int CompareSegments(PathSegment left, PathSegment right)
    {
        if (left.Name is null && right.Name is null)
        {
            return left.Index.CompareTo(right.Index);
        }

        return string.CompareOrdinal(left.Name ?? "", right.Name ?? "");
    }
}
