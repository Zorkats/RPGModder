namespace RPGModder.Core.Merge;

/// <summary>
/// One file a deploy writes. <c>Text</c> is written as UTF-8 without a byte order mark; <c>ModFile</c> means copy
/// <c>SourceFile</c> of mod <c>ModId</c>. <c>DiskPath</c> is set only for a name the engine generates (from 2r).
/// </summary>
public sealed record MergedFile(
    string File,
    MergedFileSource Source,
    string? Text,
    string? ModId,
    string? SourceFile,
    IReadOnlyList<string> ModIds,
    string? DiskPath = null);
