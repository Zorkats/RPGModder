using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

public sealed record GameSession(
    string ExecutablePath,
    string GameRoot,
    GameWorkspacePaths Workspace,
    ModEngine Engine);

public interface IGameSessionService
{
    Task<GameSession> OpenAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<OperationResult> DeployAsync(GameSession session, ModProfile profile, CancellationToken cancellationToken = default);
}

public sealed class GameSessionService : IGameSessionService
{
    public async Task<GameSession> OpenAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The game executable does not exist.", executablePath);
        }

        string fullExecutablePath = Path.GetFullPath(executablePath);
        string gameRoot = Path.GetDirectoryName(fullExecutablePath)
                          ?? throw new InvalidDataException("The game executable has no parent directory.");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = new ModEngine(fullExecutablePath);
            engine.InitializeSafeState();
            return new GameSession(
                fullExecutablePath,
                gameRoot,
                new GameWorkspacePaths(gameRoot),
                engine);
        }, cancellationToken);
    }

    public Task<OperationResult> DeployAsync(
        GameSession session,
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return session.Engine.RebuildGame(profile);
        }, cancellationToken);
    }
}
