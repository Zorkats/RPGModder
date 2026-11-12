namespace RPGModder.Core.Merge;

/// <summary>The places an added id occupies in a change set, so an offer can be matched to its <c>IdClash</c>.</summary>
internal static class RemapPlaces
{
    /// <summary>
    /// True when <paramref name="remap"/> moves the id <paramref name="collision"/> is about: a map event is the
    /// record <c>event:n</c> of its map; a map is its file on <c>*</c> and its <c>MapInfos</c> record; a database
    /// record is the record <c>id:n</c> of its file.
    /// </summary>
    public static bool Settles(IdRemap remap, Collision collision)
    {
        if (collision.Kind != CollisionKind.IdClash || !collision.ModIds.Contains(remap.ModId, StringComparer.Ordinal))
        {
            return false;
        }

        RecordKey key = collision.Key;
        return remap.Kind switch
        {
            IdKind.MapEvent => key.File == remap.Scope && key.Record == RecordKey.ForEvent(remap.FromId),
            IdKind.Map => (key.File == RpgDataSchema.MapKey(remap.FromId) && key.IsWholeFile) ||
                          (key.File == RpgDataSchema.MapInfosKey && key.Record == RecordKey.ForIndex(remap.FromId)),
            _ => RpgDataSchema.DatabaseFileOf(remap.Kind) is { } file && key.File == file && key.Record == RecordKey.ForIndex(remap.FromId)
        };
    }
}
