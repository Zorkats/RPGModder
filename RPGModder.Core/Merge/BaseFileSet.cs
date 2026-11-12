namespace RPGModder.Core.Merge;

/// <summary>
/// The base files of one plan, looked up by key. A path with no entry means vanilla does not have the file (GR-M9).
/// </summary>
public sealed class BaseFileSet
{
    private readonly Dictionary<string, BaseFile> _byKey;

    private BaseFileSet(Dictionary<string, BaseFile> byKey)
    {
        _byKey = byKey;
    }

    /// <summary>Two entries with one key are a caller bug, because the plan would depend on which one wins.</summary>
    public static BaseFileSet From(IEnumerable<BaseFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var byKey = new Dictionary<string, BaseFile>(StringComparer.Ordinal);
        foreach (BaseFile file in files)
        {
            string key = MergePath.ToKey(file.Path);
            if (!byKey.TryAdd(key, file))
            {
                throw new ArgumentException($"Two base files share the key {key}.", nameof(files));
            }
        }

        return new BaseFileSet(byKey);
    }

    public BaseFile? Find(string path)
    {
        return _byKey.GetValueOrDefault(MergePath.ToKey(path));
    }
}
