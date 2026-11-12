using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

/// <summary>
/// Compares one mod's <c>$plugins</c> list with vanilla's (guide 3.3, seam 11.1). Entries are records named by
/// their plugin name; where an entry sits is the <c>@after</c> pseudo-leaf, never its index (GR-C10).
/// </summary>
internal static class PluginListDiff
{
    private const string NameProperty = "name";

    public static IReadOnlyList<ChangeOp> Compare(string modId, string? sourceFile, JArray vanilla, JArray modded)
    {
        var ops = new List<ChangeOp>();
        if (JToken.DeepEquals(vanilla, modded))
        {
            return ops;
        }

        if (!TryNames(vanilla, out List<string> vanillaNames) || !TryNames(modded, out List<string> moddedNames))
        {
            // Repeated or missing names leave no record to line up: the mod's list is one value.
            ops.Add(Op(modId, RecordKey.WholeFile, "", ChangeKind.SetLeaf, modded, sourceFile));
            return ops;
        }

        var vanillaIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < vanillaNames.Count; index++)
        {
            vanillaIndex.Add(vanillaNames[index], index);
        }

        var moddedSet = new HashSet<string>(moddedNames, StringComparer.Ordinal);
        var common = new HashSet<string>(moddedNames.Where(vanillaIndex.ContainsKey), StringComparer.Ordinal);
        HashSet<string> kept = LongestCommonOrder(vanillaNames.Where(common.Contains).ToList(), moddedNames.Where(common.Contains).ToList());

        foreach (string name in vanillaNames.Where(name => !moddedSet.Contains(name)))
        {
            ops.Add(Op(modId, RecordKey.ForPlugin(name), "", ChangeKind.RemoveRecord, null, sourceFile));
        }

        for (int index = 0; index < moddedNames.Count; index++)
        {
            string name = moddedNames[index];
            string record = RecordKey.ForPlugin(name);
            var entry = (JObject)modded[index];
            JToken after = index == 0 ? JValue.CreateNull() : new JValue(moddedNames[index - 1]);

            if (!vanillaIndex.TryGetValue(name, out int vanillaPosition))
            {
                ops.Add(Op(modId, record, "", ChangeKind.AddRecord, entry, sourceFile));
                ops.Add(Op(modId, record, MergePath.AfterLeaf, ChangeKind.SetLeaf, after, sourceFile));
                continue;
            }

            ops.AddRange(DataDiff.CompareRecord(modId, RpgDataSchema.PluginListKey, sourceFile, record, (JObject)vanilla[vanillaPosition], entry));

            // The entries both lists keep, in the longest order both agree on, stay where they are; every other
            // kept entry moved. A removal or an insertion next to an entry does not move it, and a moved block
            // carries an anchor on each of its entries, so it is placed whole.
            if (!kept.Contains(name))
            {
                ops.Add(Op(modId, record, MergePath.AfterLeaf, ChangeKind.SetLeaf, after, sourceFile));
            }
        }

        return ops;
    }

    /// <summary>
    /// Applies a mod's <c>plugins_registry</c> after its own <c>plugins.js</c> (seam 11.1): an entry whose name is
    /// listed replaces it where it is, a new name is appended.
    /// </summary>
    public static void ApplyRegistry(JArray list, IReadOnlyList<PluginEntry> registry)
    {
        foreach (PluginEntry plugin in registry)
        {
            JObject entry = EntryOf(plugin);
            int existing = -1;
            for (int index = 0; index < list.Count && existing < 0; index++)
            {
                if (list[index] is JObject listed && NameOf(listed) == plugin.Name)
                {
                    existing = index;
                }
            }

            if (existing < 0)
            {
                list.Add(entry);
            }
            else
            {
                list[existing] = entry;
            }
        }
    }

    /// <summary>The editor's property order: name, status, description, parameters, then anything else the entry carries.</summary>
    internal static JObject EntryOf(PluginEntry plugin)
    {
        var parameters = new JObject();
        foreach ((string key, string value) in plugin.Parameters ?? new Dictionary<string, string>())
        {
            parameters[key] = value;
        }

        var entry = new JObject
        {
            [NameProperty] = plugin.Name,
            ["status"] = plugin.Status,
            ["description"] = plugin.Description,
            ["parameters"] = parameters
        };

        foreach ((string key, JToken value) in plugin.ExtensionData ?? new Dictionary<string, JToken>())
        {
            if (entry.Property(key, StringComparison.Ordinal) is null)
            {
                entry.Add(key, value.DeepClone());
            }
        }

        return entry;
    }

    internal static string? NameOf(JObject entry)
    {
        return entry.Property(NameProperty, StringComparison.Ordinal)?.Value is JValue { Type: JTokenType.String } name
            ? name.Value<string>()
            : null;
    }

    /// <summary>Every entry an object with a non-empty name, and no name twice.</summary>
    private static bool TryNames(JArray list, out List<string> names)
    {
        names = new List<string>(list.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JToken element in list)
        {
            string? name = element is JObject entry ? NameOf(entry) : null;
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
            {
                return false;
            }

            names.Add(name);
        }

        return true;
    }

    /// <summary>
    /// The entries of one longest common subsequence of two orders of the same names. On a tie the vanilla entry
    /// is the one dropped, so a swap of two neighbours moves the one vanilla had first.
    /// </summary>
    private static HashSet<string> LongestCommonOrder(List<string> vanilla, List<string> modded)
    {
        // suffix[i, j] is the length for vanilla[i..] and modded[j..].
        var suffix = new int[vanilla.Count + 1, modded.Count + 1];
        for (int i = vanilla.Count - 1; i >= 0; i--)
        {
            for (int j = modded.Count - 1; j >= 0; j--)
            {
                suffix[i, j] = vanilla[i] == modded[j]
                    ? suffix[i + 1, j + 1] + 1
                    : Math.Max(suffix[i + 1, j], suffix[i, j + 1]);
            }
        }

        var kept = new HashSet<string>(StringComparer.Ordinal);
        int left = 0;
        int right = 0;
        while (left < vanilla.Count && right < modded.Count)
        {
            if (vanilla[left] == modded[right])
            {
                kept.Add(vanilla[left]);
                left++;
                right++;
            }
            else if (suffix[left + 1, right] >= suffix[left, right + 1])
            {
                left++;
            }
            else
            {
                right++;
            }
        }

        return kept;
    }

    private static ChangeOp Op(string modId, string record, string path, ChangeKind kind, JToken? value, string? sourceFile)
    {
        return new ChangeOp(modId, new RecordKey(RpgDataSchema.PluginListKey, record), path, kind, value?.DeepClone(), sourceFile);
    }
}
