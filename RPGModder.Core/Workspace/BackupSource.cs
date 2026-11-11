namespace RPGModder.Core.Workspace;

/// <summary>
/// One file the vanilla backup takes: its content-relative path, where the copy comes from, and the
/// store's SHA-1 the copy must equal when the file was proven against the store's list (TX-23).
/// </summary>
public sealed record BackupSource(string RelativePath, string SourcePath, string? StoreSha1);
