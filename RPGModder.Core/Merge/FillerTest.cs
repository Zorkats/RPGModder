using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Merge;

/// <summary>
/// GR-R7: an added database record is filler when it has the shape of an editor blank
/// (<see cref="RpgDataSchema.HasBlankShape"/>) and its own mod names its id nowhere: no event command, record field,
/// text code, free text or plugin text code. A blank the mod fills while the game runs (an actor named by 303 or
/// drawn by 322) is a real record. The mod's references are read once, at its first blank-shaped record.
/// </summary>
internal sealed class FillerTest
{
    private readonly ModChangeSet _changeSet;
    private ModReferences? _references;

    public FillerTest(ModChangeSet changeSet)
    {
        _changeSet = changeSet;
    }

    public bool IsFiller(RecordKey key, JToken? record)
    {
        if (RpgDataSchema.DatabaseKindOf(key.File) is not { } kind ||
            !RecordKey.TryGetIndex(key.Record, out int id) ||
            !RpgDataSchema.HasBlankShape(key.File, record))
        {
            return false;
        }

        // Read as a dual-wield game on no known engine, so an id of either possible kind counts as used.
        _references ??= ReferenceMap.Scan(_changeSet, [], dualWield: true, mz: false);
        return !_references.Uses(kind, id);
    }
}
