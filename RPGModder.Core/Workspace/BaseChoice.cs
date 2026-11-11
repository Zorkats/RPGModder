namespace RPGModder.Core.Workspace;

/// <summary>The player's answer when the live folder differs from the store's files (TX-24).</summary>
public enum BaseChoice
{
    /// <summary>The default: the backup holds the store's files only (TX-23).</summary>
    UseStoreFiles,

    /// <summary>The backup is the live folder as it stands, recorded as <c>player_modified</c>.</summary>
    KeepMyFiles
}
