namespace RPGModder.Core.Platform;

/// <summary>
/// Tells whether the game of a session is running. Injected so tests can stand in for a running game.
/// </summary>
public interface IGameProcessProbe
{
    bool IsRunning(string executablePath);
}
