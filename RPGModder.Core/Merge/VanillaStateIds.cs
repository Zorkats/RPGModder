using Newtonsoft.Json.Linq;
using RPGModder.Core.Services;

namespace RPGModder.Core.Merge;

/// <summary>
/// The switch and variable ids vanilla names in <c>System.json</c> or refers to through the reference table, from 1 up
/// to the remap limit (GR-R6). A mod never moves onto one, and two mods that share one are no overlap: vanilla owns it.
/// <c>SwitchCount</c> and <c>VariableCount</c> are the lengths of vanilla's lists of names, slot 0 included. Free text
/// names no kind, so it does not count (GR-R15). Lane A runs <see cref="Scan"/> once per vanilla version and keeps the
/// result with <see cref="ScanVersion"/> and the hashes of the files it read.
/// </summary>
public sealed class VanillaStateIds
{
    /// <summary>Raised when what <see cref="Scan"/> reads changes, so a kept result is scanned again.</summary>
    public const int ScanVersion = 1;

    public VanillaStateIds(IReadOnlyList<int> switches, IReadOnlyList<int> variables, int switchCount, int variableCount)
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(variables);
        Switches = switches;
        Variables = variables;
        SwitchCount = switchCount;
        VariableCount = variableCount;
    }

    /// <summary>The switch ids vanilla uses, ascending.</summary>
    public IReadOnlyList<int> Switches { get; }

    /// <summary>The variable ids vanilla uses, ascending.</summary>
    public IReadOnlyList<int> Variables { get; }

    /// <summary>The length of vanilla's list of switch names, slot 0 included.</summary>
    public int SwitchCount { get; }

    /// <summary>The length of vanilla's list of variable names, slot 0 included.</summary>
    public int VariableCount { get; }

    /// <summary>
    /// Reads every data file in <paramref name="dataFiles"/> (other kinds are skipped) and returns the ids vanilla
    /// uses, or <c>null</c> when a data file cannot be read, because a missed id could let a mod move onto it.
    /// </summary>
    public static VanillaStateIds? Scan(IEnumerable<BaseFile> dataFiles, RpgMakerEngine? engine)
    {
        ArgumentNullException.ThrowIfNull(dataFiles);

        var ops = new List<ChangeOp>();
        int switchCount = 0;
        int variableCount = 0;
        foreach (BaseFile file in dataFiles.OrderBy(file => MergePath.ToKey(file.Path), StringComparer.Ordinal))
        {
            string key = MergePath.ToKey(file.Path);
            if (RpgDataSchema.KindOf(key) != MergeFileKind.Data)
            {
                continue;
            }

            if (!ChangeSetBuilder.TryReadBase(file, out JToken? root, out _))
            {
                return null;
            }

            if (key == RpgDataSchema.SystemFileKey)
            {
                switchCount = (root[RpgDataSchema.SwitchNamesProperty] as JArray)?.Count ?? 0;
                variableCount = (root[RpgDataSchema.VariableNamesProperty] as JArray)?.Count ?? 0;
            }

            // Vanilla reads as one mod that adds every file whole, so the walker sees each place the table names.
            ops.Add(new ChangeOp("", new RecordKey(key, RecordKey.WholeFile), "", ChangeKind.AddRecord, root, null));
        }

        ModReferences references = ReferenceMap.Scan(new ModChangeSet("", 0, ops, []), [], dualWield: true, mz: engine == RpgMakerEngine.MZ);
        return new VanillaStateIds(
            IdsOf(references, IdKind.Switch, engine),
            IdsOf(references, IdKind.Variable, engine),
            switchCount,
            variableCount);
    }

    /// <summary>The ids of <paramref name="kind"/> vanilla uses, ascending.</summary>
    public IReadOnlyList<int> Of(IdKind kind)
    {
        return kind switch
        {
            IdKind.Switch => Switches,
            IdKind.Variable => Variables,
            _ => []
        };
    }

    /// <summary>The length of vanilla's list of names of <paramref name="kind"/>, slot 0 included.</summary>
    public int CountOf(IdKind kind)
    {
        return kind switch
        {
            IdKind.Switch => SwitchCount,
            IdKind.Variable => VariableCount,
            _ => 0
        };
    }

    private static List<int> IdsOf(ModReferences references, IdKind kind, RpgMakerEngine? engine)
    {
        int limit = RpgDataSchema.RemapLimit(kind, engine) ?? 0;
        return references.StateIds(kind, limit).ToList();
    }
}
