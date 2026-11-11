namespace RPGModder.Core.Platform;

/// <summary>A source for games that are not store installs, and for code paths that never consult the store.</summary>
public sealed class NoStoreManifestSource : IStoreManifestSource
{
    public const string NotAStoreInstall = "The game is not a Steam install.";

    public StoreManifestResult Read(string gameRoot, string contentRoot)
    {
        return StoreManifestResult.Unavailable(NotAStoreInstall);
    }

    public bool RequestFetch(string appId)
    {
        return false;
    }
}
