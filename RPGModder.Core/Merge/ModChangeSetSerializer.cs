using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using System.Diagnostics.CodeAnalysis;

namespace RPGModder.Core.Merge;

/// <summary>
/// The stable text form of a change set for lane A's cache (GR-M11). The text holds no priority, because the
/// priority belongs to the profile; <see cref="Deserialize"/> takes the current one.
/// </summary>
public static class ModChangeSetSerializer
{
    public static string Serialize(ModChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);

        var ops = new JArray();
        foreach (ChangeOp op in changeSet.Ops)
        {
            var entry = new JObject
            {
                ["file"] = op.Key.File,
                ["record"] = op.Key.Record,
                ["path"] = op.Path,
                ["kind"] = op.Kind.ToString()
            };

            // An absent "value" is a C# null (the remove kinds); a JSON null leaf is written as null.
            if (op.Value is not null)
            {
                entry["value"] = op.Value.DeepClone();
            }

            if (op.SourceFile is not null)
            {
                entry["source"] = op.SourceFile;
            }

            ops.Add(entry);
        }

        var diagnostics = new JArray();
        foreach (OperationDiagnostic diagnostic in changeSet.Diagnostics)
        {
            var entry = new JObject
            {
                ["code"] = diagnostic.Code,
                ["message"] = diagnostic.Message,
                ["severity"] = diagnostic.Severity.ToString()
            };

            if (diagnostic.Subject is not null)
            {
                entry["subject"] = diagnostic.Subject;
            }

            diagnostics.Add(entry);
        }

        var root = new JObject
        {
            ["schema"] = RpgDataSchema.SchemaVersion,
            ["mod"] = changeSet.ModId,
            ["ops"] = ops,
            ["diagnostics"] = diagnostics
        };

        return MergeJson.Write(root);
    }

    /// <summary>
    /// Returns <c>null</c> for malformed text, for text written under another <see cref="RpgDataSchema.SchemaVersion"/>,
    /// and for any op <see cref="ChangeSetBuilder"/> could not have written, so a damaged cache is rebuilt, not deployed.
    /// </summary>
    public static ModChangeSet? Deserialize(string text, int priority)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return Read(MergeJson.Parse(text), priority);
        }
        catch (JsonException)
        {
            // The cache is a speed-up only: unreadable text means "rebuild", which the null return tells lane A.
            return null;
        }
    }

    private static ModChangeSet? Read(JToken token, int priority)
    {
        if (token is not JObject root ||
            root["schema"] is not JValue { Type: JTokenType.Integer, Value: long schema } ||
            schema != RpgDataSchema.SchemaVersion ||
            !TryString(root["mod"], out string? modId) ||
            root["ops"] is not JArray opsArray ||
            root["diagnostics"] is not JArray diagnosticsArray)
        {
            return null;
        }

        var ops = new List<ChangeOp>();
        foreach (JToken item in opsArray)
        {
            ChangeOp? op = ReadOp(item, modId);
            if (op is null)
            {
                return null;
            }

            ops.Add(op);
        }

        var diagnostics = new List<OperationDiagnostic>();
        foreach (JToken item in diagnosticsArray)
        {
            if (item is not JObject entry ||
                !TryString(entry["code"], out string? code) ||
                !TryString(entry["message"], out string? message) ||
                !TryEnum(entry["severity"], out DiagnosticSeverity severity) ||
                !TryOptionalString(entry, "subject", out string? subject))
            {
                return null;
            }

            diagnostics.Add(new OperationDiagnostic(code, message, severity, subject));
        }

        return new ModChangeSet(modId, priority, ops, diagnostics);
    }

    private static ChangeOp? ReadOp(JToken item, string modId)
    {
        if (item is not JObject entry ||
            !TryString(entry["file"], out string? file) ||
            !TryString(entry["record"], out string? record) ||
            !TryString(entry["path"], out string? path) ||
            !TryEnum(entry["kind"], out ChangeKind kind) ||
            !TryOptionalString(entry, "source", out string? source))
        {
            return null;
        }

        JToken? value = entry.TryGetValue("value", StringComparison.Ordinal, out JToken? stored) ? stored.DeepClone() : null;
        var op = new ChangeOp(modId, new RecordKey(file, record), path, kind, value, source);
        return IsWellFormed(op) ? op : null;
    }

    /// <summary>
    /// The shapes the builder writes: a canonical file key and path, a record form the file's family uses, and a
    /// kind and value that fit both (seam 11.1).
    /// </summary>
    private static bool IsWellFormed(ChangeOp op)
    {
        if (MergePath.ToKey(op.Key.File) != op.Key.File)
        {
            return false;
        }

        MergeFileKind family = RpgDataSchema.KindOf(op.Key.File);
        bool whole = op.Key.IsWholeFile;
        bool isPlugin = RecordKey.TryGetPlugin(op.Key.Record, out _);
        bool isRecord = family switch
        {
            MergeFileKind.Data when RpgDataSchema.IsMapFile(op.Key.File) => RecordKey.TryGetEvent(op.Key.Record, out _),
            MergeFileKind.Data => RecordKey.TryGetIndex(op.Key.Record, out _),
            MergeFileKind.PluginList => isPlugin,
            _ => false
        };

        if (!isRecord && !whole)
        {
            return false;
        }

        if (op.Path == MergePath.AfterLeaf)
        {
            // The order pseudo-leaf: only on a plugin entry, naming the entry before it or nothing.
            return isPlugin && op.Kind == ChangeKind.SetLeaf && op.Value?.Type is JTokenType.String or JTokenType.Null;
        }

        if (!IsCanonicalPath(op.Path))
        {
            return false;
        }

        bool wholePlace = op.Path.Length == 0;
        return family switch
        {
            MergeFileKind.Opaque =>
                op.Kind == ChangeKind.ReplaceFile && wholePlace && op.Value?.Type == JTokenType.String && op.SourceFile is not null,

            // One value per top-level key of the config object.
            MergeFileKind.ConfigSave => op.Kind == ChangeKind.SetLeaf && op.Value is not null && MergePath.Parse(op.Path).Count == 1,

            // The whole list as one value on *, or an entry's own record ops.
            MergeFileKind.PluginList when whole => op.Kind == ChangeKind.SetLeaf && wholePlace && op.Value is JArray,
            MergeFileKind.PluginList => op.Kind switch
            {
                ChangeKind.AddRecord => wholePlace && op.Value is JObject,
                ChangeKind.RemoveRecord => wholePlace && op.Value is null,
                ChangeKind.SetLeaf => !wholePlace && op.Value is not null,
                ChangeKind.RemoveLeaf => !wholePlace && op.Value is null,
                _ => false
            },

            _ => op.Kind switch
            {
                // A whole file with no vanilla counterpart is an AddRecord on * (GR-M9).
                ChangeKind.AddRecord => wholePlace && op.Value is not null,
                ChangeKind.RemoveRecord => isRecord && wholePlace && op.Value is null,
                ChangeKind.RemoveLeaf => !wholePlace && op.Value is null,
                ChangeKind.SetLeaf => op.Value is not null,
                ChangeKind.EditSequence or ChangeKind.EditCommands => !wholePlace && op.Value is JArray,
                _ => false
            }
        };
    }

    private static bool IsCanonicalPath(string path)
    {
        try
        {
            return MergePath.Format(MergePath.Parse(path)) == path;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryString(JToken? token, [NotNullWhen(true)] out string? value)
    {
        value = token is JValue { Type: JTokenType.String } text ? text.Value<string>() : null;
        return value is not null;
    }

    private static bool TryOptionalString(JObject entry, string name, out string? value)
    {
        value = null;
        if (!entry.TryGetValue(name, StringComparison.Ordinal, out JToken? token) || token.Type == JTokenType.Null)
        {
            return true;
        }

        return TryString(token, out value);
    }

    /// <summary>Only a member's exact name: no number, no comma list of flags, no spaces, no other case.</summary>
    private static bool TryEnum<TEnum>(JToken? token, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return TryString(token, out string? text) &&
               Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal) &&
               Enum.TryParse(text, ignoreCase: false, out value);
    }
}
