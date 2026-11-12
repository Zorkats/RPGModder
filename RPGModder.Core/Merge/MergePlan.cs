using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

/// <summary>
/// The result of <see cref="Composer.Compose"/>. <c>Ops</c> is sorted by file, record and path; <c>Collisions</c>
/// by id; <c>Outcomes</c> follows <c>Collisions</c>. <c>MergeHash</c> covers <c>Ops</c> and <c>Outcomes</c>.
/// </summary>
public sealed record MergePlan(
    IReadOnlyList<MergedOp> Ops,
    IReadOnlyList<Collision> Collisions,
    IReadOnlyList<CollisionOutcome> Outcomes,
    IReadOnlyList<OperationDiagnostic> Diagnostics,
    bool IsDeployable,
    string MergeHash);
