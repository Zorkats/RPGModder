namespace RPGModder.Core.Platform;

/// <summary>
/// One file of the store's list (TX-22): its path relative to the content root with forward
/// slashes, its size, and its SHA-1 as upper-case hex.
/// </summary>
public sealed record StoreFileEntry(string Path, long Size, string Sha1);
