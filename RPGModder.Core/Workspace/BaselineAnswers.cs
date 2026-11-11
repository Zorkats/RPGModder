namespace RPGModder.Core.Workspace;

/// <summary>
/// The answers a vanilla build has so far. A build without the answer it needs changes nothing and
/// returns the question; the caller asks the player and runs the build again with the answer.
/// </summary>
public sealed record BaselineAnswers(bool ModMatchesConfirmed = false, BaseChoice? Base = null)
{
    public static BaselineAnswers None { get; } = new();
}
