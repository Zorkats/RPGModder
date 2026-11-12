using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace RPGModder.Core.Merge;

/// <summary>
/// Turns one mod into ops against vanilla (guide 3.1, first step). A whole-file mod and a <c>json_patches</c> mod
/// go through the same walker, so they give the same kind of ops (GR-M4). Bad mod content is a diagnostic,
/// never an exception and never a fallback (GR-C5, GR-M8).
/// </summary>
public static class ChangeSetBuilder
{
    public static ModChangeSet Build(ModSource mod, BaseFileSet baseFiles)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(baseFiles);

        var ops = new List<ChangeOp>();
        var diagnostics = new List<OperationDiagnostic>();

        foreach (TargetEdits target in CollectTargets(mod))
        {
            switch (RpgDataSchema.KindOf(target.Key))
            {
                case MergeFileKind.Data:
                    BuildDataFile(mod.ModId, target, baseFiles, ops, diagnostics);
                    break;
                case MergeFileKind.PluginList:
                    BuildPluginList(mod, target, baseFiles, ops, diagnostics);
                    break;
                case MergeFileKind.ConfigSave:
                    BuildConfigSave(mod.ModId, target, baseFiles, ops, diagnostics);
                    break;
                case MergeFileKind.Opaque:
                    BuildOpaqueFile(mod.ModId, target, baseFiles, ops, diagnostics);
                    break;
                default:
                    throw new UnreachableException($"KindOf returned a kind the builder does not know for {target.Key}.");
            }
        }

        ops.Sort(CompareOps);
        return new ModChangeSet(mod.ModId, mod.Priority, ops, diagnostics);
    }

    internal static int CompareOps(ChangeOp left, ChangeOp right)
    {
        int result = string.CompareOrdinal(left.Key.File, right.Key.File);
        if (result == 0)
        {
            result = string.CompareOrdinal(left.Key.Record, right.Key.Record);
        }

        if (result == 0)
        {
            result = string.CompareOrdinal(left.Path, right.Path);
        }

        return result;
    }

    private static void BuildDataFile(
        string modId,
        TargetEdits target,
        BaseFileSet baseFiles,
        List<ChangeOp> ops,
        List<OperationDiagnostic> diagnostics)
    {
        BaseFile? baseFile = baseFiles.Find(target.Key);
        if (baseFile is null)
        {
            BuildNewDataFile(modId, target, ops, diagnostics);
            return;
        }

        if (!TryReadBase(baseFile, out JToken? vanilla, out OperationDiagnostic? baseProblem))
        {
            diagnostics.Add(baseProblem);
            return;
        }

        // Vanilla is cloned only for a patch-only mod; a mod that ships the file is read instead.
        JToken? modded;
        if (target.File is null)
        {
            modded = vanilla.DeepClone();
        }
        else if (!TryReadModData(modId, target, target.File, out modded, diagnostics))
        {
            return;
        }

        if (!TryApplyPatches(modId, target, modded, diagnostics))
        {
            return;
        }

        if (RpgDataSchema.ShapeOf(target.Key, vanilla) == RootShape.Map &&
            RpgDataSchema.IsEventList(vanilla[RpgDataSchema.EventsProperty]) &&
            modded is JObject map &&
            map[RpgDataSchema.EventsProperty] is not JArray)
        {
            // Vanilla's events are records other mods edit one by one; a mod without an events list would
            // replace them all with no collision, so it is refused like a root of the wrong shape.
            diagnostics.Add(Error(
                DiagnosticCodes.MergeRootShapeMismatch,
                $"Mod {modId} gives {target.Key} an events value that is not a list.",
                modId));
            return;
        }

        ops.AddRange(DataDiff.Compare(modId, target.Key, target.File?.Source, vanilla, modded));
    }

    /// <summary>
    /// GR-M9: a data file vanilla lacks is one <c>AddRecord</c> on <c>*</c>, never merged from an empty base. Its
    /// source stays on the op so the writer copies the mod's bytes, unless a patch changed the content.
    /// </summary>
    private static void BuildNewDataFile(string modId, TargetEdits target, List<ChangeOp> ops, List<OperationDiagnostic> diagnostics)
    {
        if (target.File is null)
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergePatchTargetMissing,
                $"Mod {modId} patches {target.Key}, which the game does not have.",
                modId));
            return;
        }

        if (!TryReadModData(modId, target, target.File, out JToken? content, diagnostics) ||
            !TryApplyPatches(modId, target, content, diagnostics))
        {
            return;
        }

        string? source = target.Patches.Count == 0 ? target.File.Source : null;
        ops.Add(new ChangeOp(modId, new RecordKey(target.Key, RecordKey.WholeFile), "", ChangeKind.AddRecord, content, source));
    }

    /// <summary>
    /// Bytes are compared by hash only (guide 3.3): a file equal to vanilla is no change, anything else replaces
    /// the whole file. A patch cannot apply to bytes.
    /// </summary>
    private static void BuildOpaqueFile(
        string modId,
        TargetEdits target,
        BaseFileSet baseFiles,
        List<ChangeOp> ops,
        List<OperationDiagnostic> diagnostics)
    {
        if (!RejectPatches(modId, target, diagnostics) || target.File is null)
        {
            return;
        }

        string hash = target.File.Sha256.ToLowerInvariant();
        if (string.Equals(baseFiles.Find(target.Key)?.Sha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ops.Add(new ChangeOp(
            modId,
            new RecordKey(target.Key, RecordKey.WholeFile),
            "",
            ChangeKind.ReplaceFile,
            new JValue(hash),
            target.File.Source));
    }

    /// <summary>
    /// The mod's list is its own <c>plugins.js</c>, or vanilla's when it ships none, with its
    /// <c>plugins_registry</c> applied on top (seam 11.1). No vanilla <c>plugins.js</c> is an empty list.
    /// </summary>
    private static void BuildPluginList(
        ModSource mod,
        TargetEdits target,
        BaseFileSet baseFiles,
        List<ChangeOp> ops,
        List<OperationDiagnostic> diagnostics)
    {
        string modId = mod.ModId;
        if (!RejectPatches(modId, target, diagnostics))
        {
            return;
        }

        PluginListText? vanilla = PluginListText.Empty();
        BaseFile? baseFile = baseFiles.Find(target.Key);
        if (baseFile is not null && !PluginListText.TryParse(baseFile.Text, out vanilla, out string? baseError))
        {
            diagnostics.Add(Error(DiagnosticCodes.MergeBaseParseFailed, $"The game file {target.Key} cannot be read: {baseError}.", target.Key));
            return;
        }

        JArray list;
        if (target.File is null)
        {
            list = (JArray)vanilla.Entries.DeepClone();
        }
        else if (PluginListText.TryParse(target.File.Text, out PluginListText? own, out string? modError))
        {
            list = own.Entries;
        }
        else
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergeParseFailed,
                $"Mod {modId} file {target.File.Source} for {target.Key} cannot be read: {modError}.",
                modId));
            return;
        }

        var named = new List<PluginEntry>(mod.Plugins.Count);
        for (int index = 0; index < mod.Plugins.Count; index++)
        {
            PluginEntry? plugin = mod.Plugins[index];
            if (string.IsNullOrEmpty(plugin?.Name))
            {
                // A nameless entry has no record to merge, and a silent whole-list fallback would hide the fault.
                diagnostics.Add(Error(
                    DiagnosticCodes.MergeParseFailed,
                    $"Mod {modId} plugins_registry entry {index} has no name.",
                    modId));
                continue;
            }

            named.Add(plugin);
        }

        PluginListDiff.ApplyRegistry(list, named);
        ops.AddRange(PluginListDiff.Compare(modId, target.File?.Source, vanilla.Entries, list));
    }

    /// <summary>
    /// GR-M10: the base is the live file (or <c>{}</c>), and each top-level key the mod sets to another value is
    /// one op. A key the mod lacks is the player's and is never removed.
    /// </summary>
    private static void BuildConfigSave(
        string modId,
        TargetEdits target,
        BaseFileSet baseFiles,
        List<ChangeOp> ops,
        List<OperationDiagnostic> diagnostics)
    {
        if (!RpgDataSchema.IsDecodedConfigSave(target.Key))
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergeConfigSaveUnsupported,
                $"Mod {modId} changes {target.Key}, a config save form this version cannot read yet.",
                modId));
            return;
        }

        if (!TryReadConfigBase(baseFiles.Find(target.Key), target.Key, out JObject? live, out OperationDiagnostic? baseProblem))
        {
            diagnostics.Add(baseProblem);
            return;
        }

        JObject? modded = (JObject)live.DeepClone();
        if (target.File is not null && !ConfigSaveText.TryDecode(target.Key, target.File.Text, out modded, out string? modError))
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergeParseFailed,
                $"Mod {modId} file {target.File.Source} for {target.Key} cannot be read: {modError}.",
                modId));
            return;
        }

        foreach (ModPatch patch in target.Patches)
        {
            ApplyPatch(modded, patch.MergeData);
        }

        foreach (JProperty property in modded.Properties())
        {
            JToken? current = live.Property(property.Name, StringComparison.Ordinal)?.Value;
            if (current is null || !JToken.DeepEquals(current, property.Value))
            {
                ops.Add(new ChangeOp(
                    modId,
                    new RecordKey(target.Key, RecordKey.WholeFile),
                    MergePath.Child("", property.Name),
                    ChangeKind.SetLeaf,
                    property.Value.DeepClone(),
                    target.File?.Source));
            }
        }
    }

    /// <summary>The live config save, or an empty object when the player has none yet (GR-M10).</summary>
    internal static bool TryReadConfigBase(
        BaseFile? baseFile,
        string key,
        [NotNullWhen(true)] out JObject? live,
        [NotNullWhen(false)] out OperationDiagnostic? problem)
    {
        problem = null;
        if (baseFile is null)
        {
            live = new JObject();
            return true;
        }

        if (ConfigSaveText.TryDecode(key, baseFile.Text, out live, out string? error))
        {
            return true;
        }

        problem = Error(DiagnosticCodes.MergeBaseParseFailed, $"The config save {key} cannot be read: {error}.", key);
        return false;
    }

    private static bool TryReadModData(
        string modId,
        TargetEdits target,
        ModFile file,
        [NotNullWhen(true)] out JToken? content,
        List<OperationDiagnostic> diagnostics)
    {
        if (!TryParse(file.Text, out content, out string? modError))
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergeParseFailed,
                $"Mod {modId} file {file.Source} for {target.Key} is not valid JSON ({modError}).",
                modId));
            return false;
        }

        if (RpgDataSchema.ExpectedRootOf(target.Key) is JTokenType expected && content.Type != expected)
        {
            diagnostics.Add(Error(
                DiagnosticCodes.MergeRootShapeMismatch,
                $"Mod {modId} file {file.Source} for {target.Key} has {Describe(content.Type)} at its root, but the game reads {Describe(expected)}.",
                modId));
            content = null;
            return false;
        }

        return true;
    }

    private static bool TryApplyPatches(string modId, TargetEdits target, JToken content, List<OperationDiagnostic> diagnostics)
    {
        foreach (ModPatch patch in target.Patches)
        {
            if (content is not JObject root)
            {
                diagnostics.Add(Error(
                    DiagnosticCodes.MergePatchShapeMismatch,
                    $"Mod {modId} patches {target.Key} with an object, but the file's root is not an object.",
                    modId));
                return false;
            }

            ApplyPatch(root, patch.MergeData);
        }

        return true;
    }

    /// <summary><c>json_patches</c> apply to JSON data and to config saves; on a plugin list or bytes they are a shape mismatch.</summary>
    private static bool RejectPatches(string modId, TargetEdits target, List<OperationDiagnostic> diagnostics)
    {
        if (target.Patches.Count == 0)
        {
            return true;
        }

        diagnostics.Add(Error(
            DiagnosticCodes.MergePatchShapeMismatch,
            $"Mod {modId} patches {target.Key} with an object, but that file is not a JSON data file.",
            modId));
        return false;
    }

    /// <summary>
    /// Groups a mod's edits by file key, in key order. When <c>file_ops</c> names one target twice the later entry
    /// wins, as the copy order does; patches apply after the file, in manifest order. A mod that registers plugins
    /// changes the plugin list even when it ships no <c>plugins.js</c>.
    /// </summary>
    private static IEnumerable<TargetEdits> CollectTargets(ModSource mod)
    {
        var targets = new SortedDictionary<string, TargetEdits>(StringComparer.Ordinal);
        foreach (ModFile file in mod.Files)
        {
            TargetEdits target = GetTarget(targets, file.Target);
            target.File = file;
        }

        foreach (ModPatch patch in mod.Patches)
        {
            GetTarget(targets, patch.Target).Patches.Add(patch);
        }

        if (mod.Plugins.Count > 0)
        {
            GetTarget(targets, RpgDataSchema.PluginListKey);
        }

        return targets.Values;
    }

    private static TargetEdits GetTarget(SortedDictionary<string, TargetEdits> targets, string path)
    {
        string key = MergePath.ToKey(path);
        if (!targets.TryGetValue(key, out TargetEdits? target))
        {
            target = new TargetEdits(key);
            targets.Add(key, target);
        }

        return target;
    }

    /// <summary>The <c>json_patches</c> rule of 1.x: objects merge property by property, anything else replaces.</summary>
    private static void ApplyPatch(JObject target, JObject patch)
    {
        foreach (JProperty property in patch.Properties())
        {
            JProperty? existing = target.Property(property.Name, StringComparison.Ordinal);
            if (existing is null)
            {
                target.Add(property.Name, property.Value.DeepClone());
            }
            else if (existing.Value is JObject existingObject && property.Value is JObject patchObject)
            {
                ApplyPatch(existingObject, patchObject);
            }
            else
            {
                existing.Value = property.Value.DeepClone();
            }
        }
    }

    /// <summary>
    /// Reads a game file the way every step reads it, so the same fault always gives the same diagnostic and
    /// <see cref="Composer"/> can fold the copies (seam section 11). The subject is the file key.
    /// </summary>
    internal static bool TryReadBase(
        BaseFile baseFile,
        [NotNullWhen(true)] out JToken? root,
        [NotNullWhen(false)] out OperationDiagnostic? problem)
    {
        string key = MergePath.ToKey(baseFile.Path);
        problem = null;
        if (!TryParse(baseFile.Text, out root, out string? error))
        {
            problem = Error(DiagnosticCodes.MergeBaseParseFailed, $"The game file {key} is not valid JSON ({error}).", key);
            return false;
        }

        if (RpgDataSchema.ExpectedRootOf(key) is JTokenType expected && root.Type != expected)
        {
            problem = Error(
                DiagnosticCodes.MergeRootShapeMismatch,
                $"The game file {key} has {Describe(root.Type)} at its root, but the game reads {Describe(expected)}.",
                key);
            root = null;
            return false;
        }

        return true;
    }

    private static string Describe(JTokenType type)
    {
        return type switch
        {
            JTokenType.Object => "an object",
            JTokenType.Array => "an array",
            _ => "a value of type " + type.ToString().ToLowerInvariant()
        };
    }

    private static bool TryParse(string? text, [NotNullWhen(true)] out JToken? token, out string? error)
    {
        token = null;
        if (text is null)
        {
            error = "no text";
            return false;
        }

        try
        {
            token = MergeJson.Parse(text);
            error = null;
            return true;
        }
        catch (JsonReaderException exception)
        {
            error = $"line {exception.LineNumber}, position {exception.LinePosition}";
            return false;
        }
    }

    private static OperationDiagnostic Error(string code, string message, string subject)
    {
        return new OperationDiagnostic(code, message, DiagnosticSeverity.Error, subject);
    }

    private sealed class TargetEdits
    {
        public TargetEdits(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public ModFile? File { get; set; }

        public List<ModPatch> Patches { get; } = new();
    }
}
