using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Merge;

/// <summary>
/// The ids a change set adds, by kind and scope (GR-R2, GR-R3). An op the builder writes for a record vanilla does
/// not have is the only kind of added id: a map event is an <c>AddRecord</c> on <c>event:n</c> of a vanilla map; a
/// map is a whole new <c>MapNNN.json</c>, its <c>MapInfos</c> record, or both. Step 1 lists these two kinds only.
/// </summary>
internal static class AddedIdIndex
{
    public static IReadOnlyList<AddedId> Of(ModChangeSet changeSet)
    {
        var events = new List<AddedId>();
        var maps = new SortedDictionary<int, (JToken? File, JToken? Info)>();
        foreach (ChangeOp op in changeSet.Ops)
        {
            if (op.Kind != ChangeKind.AddRecord || op.Path.Length != 0 || op.Value is null)
            {
                continue;
            }

            if (op.Key.IsWholeFile && RpgDataSchema.TryGetMapId(op.Key.File, out int mapId))
            {
                maps[mapId] = (op.Value, maps.GetValueOrDefault(mapId).Info);
            }
            else if (op.Key.File == RpgDataSchema.MapInfosKey && RecordKey.TryGetIndex(op.Key.Record, out int infoId) && infoId > 0)
            {
                maps[infoId] = (maps.GetValueOrDefault(infoId).File, op.Value);
            }
            else if (RpgDataSchema.IsMapFile(op.Key.File) && RecordKey.TryGetEvent(op.Key.Record, out int eventId) && eventId > 0)
            {
                events.Add(new AddedId(IdKind.MapEvent, op.Key.File, eventId, op.Value));
            }
        }

        var result = new List<AddedId>(events);
        foreach ((int id, (JToken? file, JToken? info)) in maps)
        {
            // Both halves are the content, cloned so no op gains a parent; two mods that add one map with one tree entry are one map (GR-R3).
            result.Add(new AddedId(IdKind.Map, "", id, new JArray(file?.DeepClone() ?? JValue.CreateNull(), info?.DeepClone() ?? JValue.CreateNull())));
        }

        return result;
    }

    /// <summary>True when the change set still adds <paramref name="remap"/>'s old id; otherwise the entry is inactive (GR-R12).</summary>
    public static bool IsActive(IdRemap remap, IReadOnlyList<AddedId> added)
    {
        return added.Any(item => item.Kind == remap.Kind && item.Scope == remap.Scope && item.Id == remap.FromId);
    }
}

/// <summary>One added id and the content that decides whether two mods add the same thing.</summary>
internal sealed record AddedId(IdKind Kind, string Scope, int Id, JToken Content);
