using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Merge;

/// <summary>
/// The schema-driven walker: compares one mod's version of a data file with vanilla and returns the ops
/// (GR-M1, guide 3.3). Every decision about a file or a path comes from <see cref="RpgDataSchema"/>.
/// </summary>
internal sealed class DataDiff
{
    private readonly string _modId;
    private readonly string _fileKey;
    private readonly string? _sourceFile;
    private readonly List<ChangeOp> _ops = new();

    private DataDiff(string modId, string fileKey, string? sourceFile)
    {
        _modId = modId;
        _fileKey = fileKey;
        _sourceFile = sourceFile;
    }

    public static IReadOnlyList<ChangeOp> Compare(string modId, string fileKey, string? sourceFile, JToken vanilla, JToken modded)
    {
        var diff = new DataDiff(modId, fileKey, sourceFile);
        diff.CompareRoot(vanilla, modded);
        return diff._ops;
    }

    /// <summary>The leaves of one record that lives outside a data file: a <c>$plugins</c> entry.</summary>
    public static IReadOnlyList<ChangeOp> CompareRecord(string modId, string fileKey, string? sourceFile, string record, JObject vanilla, JObject modded)
    {
        var diff = new DataDiff(modId, fileKey, sourceFile);
        diff.CompareObject(record, "", vanilla, modded);
        return diff._ops;
    }

    private void CompareRoot(JToken vanilla, JToken modded)
    {
        if (JToken.DeepEquals(vanilla, modded))
        {
            return;
        }

        if (vanilla.Type != modded.Type)
        {
            // A root of another type has no record to line up with vanilla: one value for the whole file.
            Add(RecordKey.WholeFile, "", ChangeKind.SetLeaf, modded);
            return;
        }

        switch (RpgDataSchema.ShapeOf(_fileKey, vanilla))
        {
            case RootShape.Records:
                CompareRecords((JArray)vanilla, (JArray)modded, RecordKey.ForIndex);
                break;
            case RootShape.Map:
                CompareMap((JObject)vanilla, (JObject)modded);
                break;
            case RootShape.Object:
                CompareObject(RecordKey.WholeFile, "", (JObject)vanilla, (JObject)modded);
                break;
            default:
                Add(RecordKey.WholeFile, "", ChangeKind.SetLeaf, modded);
                break;
        }
    }

    /// <summary>
    /// A map is an object whose <c>events</c> are records of their own (guide 3.3). When vanilla's events are not
    /// a record list, <c>events</c> is one more map-level value. The builder refuses a mod whose events are not a
    /// list while vanilla's are; the second test below only keeps a direct call safe.
    /// </summary>
    private void CompareMap(JObject vanilla, JObject modded)
    {
        JToken? vanillaEvents = vanilla[RpgDataSchema.EventsProperty];
        if (!RpgDataSchema.IsEventList(vanillaEvents) || modded[RpgDataSchema.EventsProperty] is not JArray moddedEvents)
        {
            CompareObject(RecordKey.WholeFile, "", vanilla, modded);
            return;
        }

        CompareObject(RecordKey.WholeFile, "", vanilla, modded, RpgDataSchema.EventsProperty);
        CompareRecords((JArray)vanillaEvents!, moddedEvents, RecordKey.ForEvent);
    }

    private void CompareRecords(JArray vanilla, JArray modded, Func<int, string> recordOf)
    {
        int count = Math.Max(vanilla.Count, modded.Count);
        for (int index = 0; index < count; index++)
        {
            JToken? before = RecordAt(vanilla, index);
            JToken? after = RecordAt(modded, index);
            string record = recordOf(index);

            if (before is null && after is null)
            {
                continue;
            }

            if (before is null)
            {
                Add(record, "", ChangeKind.AddRecord, after);
            }
            else if (after is null)
            {
                Add(record, "", ChangeKind.RemoveRecord, null);
            }
            else if (before is JObject beforeRecord && after is JObject afterRecord)
            {
                CompareObject(record, "", beforeRecord, afterRecord);
            }
            else if (!JToken.DeepEquals(before, after))
            {
                // A record that is not an object has no leaves: it is one value.
                Add(record, "", ChangeKind.SetLeaf, after);
            }
        }
    }

    /// <summary><paramref name="skipped"/> names a property compared elsewhere, as a map's <c>events</c>.</summary>
    private void CompareObject(string record, string path, JObject vanilla, JObject modded, string? skipped = null)
    {
        foreach (JProperty property in modded.Properties())
        {
            if (property.Name == skipped || RpgDataSchema.IsVolatile(_fileKey, path, property.Name))
            {
                continue;
            }

            string child = MergePath.Child(path, property.Name);
            JProperty? original = vanilla.Property(property.Name, StringComparison.Ordinal);
            if (original is null)
            {
                Add(record, child, ChangeKind.SetLeaf, property.Value);
            }
            else
            {
                CompareValue(record, child, original.Value, property.Value);
            }
        }

        foreach (JProperty property in vanilla.Properties())
        {
            if (property.Name == skipped ||
                RpgDataSchema.IsVolatile(_fileKey, path, property.Name) ||
                modded.Property(property.Name, StringComparison.Ordinal) is not null)
            {
                continue;
            }

            Add(record, MergePath.Child(path, property.Name), ChangeKind.RemoveLeaf, null);
        }
    }

    private void CompareValue(string record, string path, JToken vanilla, JToken modded)
    {
        if (JToken.DeepEquals(vanilla, modded))
        {
            return;
        }

        if (vanilla is JObject vanillaObject && modded is JObject moddedObject && !RpgDataSchema.IsAtomicObject(_fileKey, path))
        {
            CompareObject(record, path, vanillaObject, moddedObject);
            return;
        }

        if (vanilla is JArray vanillaArray && modded is JArray moddedArray)
        {
            CompareArray(record, path, vanillaArray, moddedArray);
            return;
        }

        Add(record, path, ChangeKind.SetLeaf, modded);
    }

    private void CompareArray(string record, string path, JArray vanilla, JArray modded)
    {
        switch (RpgDataSchema.PolicyOf(_fileKey, path))
        {
            case ArrayPolicy.Sequence:
                Add(record, path, ChangeKind.EditSequence, modded);
                break;
            case ArrayPolicy.Commands:
                Add(record, path, ChangeKind.EditCommands, modded);
                break;
            case ArrayPolicy.Positional when vanilla.Count == modded.Count:
                for (int index = 0; index < vanilla.Count; index++)
                {
                    CompareValue(record, MergePath.Index(path, index), vanilla[index], modded[index]);
                }

                break;
            case ArrayPolicy.Keyed:
                CompareKeyed(record, path, vanilla, modded);
                break;
            default:
                // Atomic, and a Positional array whose length changed (one value until GR-K4 in 2b).
                Add(record, path, ChangeKind.SetLeaf, modded);
                break;
        }
    }

    private void CompareKeyed(string record, string path, JArray vanilla, JArray modded)
    {
        int count = Math.Max(vanilla.Count, modded.Count);
        for (int index = 0; index < count; index++)
        {
            string child = MergePath.Index(path, index);
            if (index >= modded.Count)
            {
                Add(record, child, ChangeKind.RemoveLeaf, null);
            }
            else if (index >= vanilla.Count)
            {
                Add(record, child, ChangeKind.SetLeaf, modded[index]);
            }
            else
            {
                CompareValue(record, child, vanilla[index], modded[index]);
            }
        }
    }

    private void Add(string record, string path, ChangeKind kind, JToken? value)
    {
        _ops.Add(new ChangeOp(_modId, new RecordKey(_fileKey, record), path, kind, value?.DeepClone(), _sourceFile));
    }

    private static JToken? RecordAt(JArray array, int index)
    {
        if (index >= array.Count || array[index].Type == JTokenType.Null)
        {
            return null;
        }

        return array[index];
    }
}
