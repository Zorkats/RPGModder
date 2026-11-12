using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

public sealed record ModChangeSet(
    string ModId,
    int Priority,
    IReadOnlyList<ChangeOp> Ops,
    IReadOnlyList<OperationDiagnostic> Diagnostics);
