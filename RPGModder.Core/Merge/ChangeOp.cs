using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Merge;

/// <summary>
/// One change a mod makes against vanilla (GR-M1). <c>Value</c> is <c>null</c> for the remove kinds; a leaf
/// set to JSON <c>null</c> holds a <see cref="JValue"/> of type null instead.
/// </summary>
public sealed record ChangeOp(
    string ModId,
    RecordKey Key,
    string Path,
    ChangeKind Kind,
    JToken? Value,
    string? SourceFile);
