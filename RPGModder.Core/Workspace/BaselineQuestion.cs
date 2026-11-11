namespace RPGModder.Core.Workspace;

/// <summary>What a vanilla build needs from the player before it can go on.</summary>
public enum BaselineQuestion
{
    /// <summary>Nothing; the build ran or failed on its own.</summary>
    None,

    /// <summary>Live files equal mod files in content and time; the player confirms the store's verification ran since (TX-21).</summary>
    ConfirmModMatches,

    /// <summary>Live files differ from the store's and nothing explains it: "Use the store's files" or "Keep my files" (TX-24).</summary>
    ChooseBase,

    /// <summary>Some files exist nowhere in a proven copy; the player's store client must fetch them (TX-23).</summary>
    StoreFetch
}
