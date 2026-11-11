namespace RPGModder.Core.Platform;

/// <summary>
/// The store's own file list for an installed game, and the one way to ask the store's client to
/// fetch files (OWN-15). Implementations only read the store's files, never write inside a store
/// folder, and never read a login, token or account file.
/// </summary>
public interface IStoreManifestSource
{
    /// <summary>Reads the store's list for the game at <paramref name="gameRoot"/>, mapped to <paramref name="contentRoot"/>.</summary>
    StoreManifestResult Read(string gameRoot, string contentRoot);

    /// <summary>
    /// Asks the player's own store client to verify the game and fetch what is missing (TX-23).
    /// Returns false when the request could not be handed to the client.
    /// </summary>
    bool RequestFetch(string appId);
}
