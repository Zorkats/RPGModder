namespace RPGModder.Core.Platform;

/// <summary>
/// The store's file list for an installed game, or the reason it is unavailable (TX-22). The list
/// is never partial: either every file of every installed depot is in <see cref="Files"/>, or
/// <see cref="IsAvailable"/> is false. <see cref="App"/> is set whenever the app manifest itself
/// could be read, even when the depot lists could not, so the backup can still record the ids (GR-V10).
/// </summary>
public sealed class StoreManifestResult
{
    private readonly Dictionary<string, StoreFileEntry> _files;

    private StoreManifestResult(StoreApp? app, IReadOnlyList<StoreFileEntry>? files, string? reason, StringComparer pathComparer)
    {
        App = app;
        Reason = reason;
        _files = new Dictionary<string, StoreFileEntry>(pathComparer);
        foreach (StoreFileEntry file in files ?? Array.Empty<StoreFileEntry>())
        {
            _files[file.Path] = file;
        }
    }

    public bool IsAvailable => Reason == null;

    /// <summary>Why the list is unavailable; null when it is available.</summary>
    public string? Reason { get; }

    public StoreApp? App { get; }

    /// <summary>Every file the store lists under the content root, ordered by path.</summary>
    public IReadOnlyList<StoreFileEntry> Files => _files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

    public static StoreManifestResult Available(StoreApp app, IReadOnlyList<StoreFileEntry> files, StringComparer pathComparer)
    {
        return new StoreManifestResult(app, files, null, pathComparer);
    }

    public static StoreManifestResult Unavailable(string reason, StoreApp? app = null)
    {
        return new StoreManifestResult(app, null, reason, StringComparer.Ordinal);
    }

    /// <summary>The store's entry for a content-relative path, compared the way the file system compares names.</summary>
    public StoreFileEntry? Find(string relativePath)
    {
        return _files.TryGetValue(relativePath.Replace('\\', '/'), out StoreFileEntry? entry) ? entry : null;
    }
}
