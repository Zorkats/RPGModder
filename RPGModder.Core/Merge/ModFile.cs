namespace RPGModder.Core.Merge;

/// <summary>One <c>file_ops</c> entry. <c>Source</c> is returned untouched so the caller can copy the file.</summary>
public sealed record ModFile(
    string Target,
    string Source,
    string? Text,
    string Sha256);
