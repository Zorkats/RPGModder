using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// The identity of one record of one file (GR-M2): <c>File</c> is the key from <see cref="MergePath.ToKey"/>,
/// <c>Record</c> is <c>id:&lt;n&gt;</c> for a database record, <c>event:&lt;n&gt;</c> for a map event,
/// <c>plugin:&lt;name&gt;</c> for a <c>$plugins</c> entry, or <c>*</c> for whole-file and root-level ops.
/// </summary>
public sealed record RecordKey(string File, string Record)
{
    /// <summary>The record of a whole-file op, and of every op in a file whose root is not a record list.</summary>
    internal const string WholeFile = "*";

    private const string RecordPrefix = "id:";
    private const string EventPrefix = "event:";
    private const string PluginPrefix = "plugin:";

    internal bool IsWholeFile => Record == WholeFile;

    internal static string ForIndex(int index)
    {
        return RecordPrefix + index.ToString(CultureInfo.InvariantCulture);
    }

    internal static string ForEvent(int index)
    {
        return EventPrefix + index.ToString(CultureInfo.InvariantCulture);
    }

    internal static string ForPlugin(string name)
    {
        return PluginPrefix + name;
    }

    /// <summary>Reads <c>id:&lt;n&gt;</c> in the one form <see cref="ForIndex"/> writes: no sign, no leading zero.</summary>
    internal static bool TryGetIndex(string record, out int index)
    {
        return TryGetNumber(record, RecordPrefix, out index);
    }

    /// <summary>Reads <c>event:&lt;n&gt;</c> in the one form <see cref="ForEvent"/> writes.</summary>
    internal static bool TryGetEvent(string record, out int index)
    {
        return TryGetNumber(record, EventPrefix, out index);
    }

    /// <summary>Reads <c>plugin:&lt;name&gt;</c>; the name is never empty.</summary>
    internal static bool TryGetPlugin(string record, out string name)
    {
        name = record.StartsWith(PluginPrefix, StringComparison.Ordinal) ? record[PluginPrefix.Length..] : "";
        return name.Length > 0;
    }

    private static bool TryGetNumber(string record, string prefix, out int index)
    {
        index = -1;
        return record.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(record.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index) &&
               prefix + index.ToString(CultureInfo.InvariantCulture) == record;
    }
}
