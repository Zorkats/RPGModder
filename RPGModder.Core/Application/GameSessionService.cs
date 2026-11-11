using System.Diagnostics;
using System.Globalization;
using RPGModder.Core.Deployment;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using RPGModder.Core.Workspace;

namespace RPGModder.Core.Application;

public sealed record GameSession(
    string ExecutablePath,
    string GameRoot,
    GameWorkspacePaths Workspace,
    ModEngine Engine,
    OperationGate Gate,
    IGameProcessProbe GameProcess);

public interface IGameSessionService
{
    Task<GameSession> OpenAsync(string executablePath, CancellationToken cancellationToken = default);
    Task<OperationResult> DeployAsync(GameSession session, string profileName, ModProfile profile, CancellationToken cancellationToken = default);
    Task<bool> HasPendingChangesAsync(GameSession session, string profileName, ModProfile profile, CancellationToken cancellationToken = default);
}

public sealed class GameSessionService : IGameSessionService
{
    private const string OpenOperation = "Open game";
    private const string DeployOperation = "Deploy";

    private readonly IGameProcessProbe _gameProcess;

    public GameSessionService()
        : this(new GameProcessProbe())
    {
    }

    public GameSessionService(IGameProcessProbe gameProcess)
    {
        _gameProcess = gameProcess;
    }

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
            var workspace = new GameWorkspacePaths(gameRoot);

            // The only place a gate is built: one per open workspace. Taking it before the engine
            // runs migration and crash recovery also takes over a lock file a dead process left.
            var gate = new OperationGate(workspace.OperationLock);
            if (!gate.TryEnter(OpenOperation, out IDisposable? lease, out OperationDiagnostic? refusal))
            {
                throw new InvalidOperationException(refusal!.Message);
            }

            using (lease)
            {
                var engine = new ModEngine(fullExecutablePath);
                engine.InitializeSafeState();
                return new GameSession(fullExecutablePath, gameRoot, workspace, engine, gate, _gameProcess);
            }
        }, cancellationToken);
    }

    public async Task<OperationResult> DeployAsync(
        GameSession session,
        string profileName,
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, DeployOperation, true, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        ModProfile snapshot = Snapshot(profile);
        using (lease)
        {
            return await Task.Run(() => Deploy(session, profileName, snapshot), CancellationToken.None);
        }
    }

    public Task<bool> HasPendingChangesAsync(
        GameSession session,
        string profileName,
        ModProfile profile,
        CancellationToken cancellationToken = default)
    {
        ModProfile snapshot = Snapshot(profile);
        return Task.Run(() => HasPendingChanges(session, profileName, snapshot), cancellationToken);
    }

    private static bool HasPendingChanges(GameSession session, string profileName, ModProfile profile)
    {
        try
        {
            DeployedState? deployed = new DeployedStateStore(session.Workspace).Read();
            if (deployed == null || !string.Equals(deployed.Profile, profileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string planHash = DeploymentFingerprint.Compute(session.Workspace, profile);
            return !string.Equals(deployed.PlanHash, planHash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A mod folder changed under the scan. Unknown reads as pending, which only asks for a deploy.
            Trace.TraceWarning($"The pending state could not be computed: {ex.Message}");
            return true;
        }
    }

    // The UI keeps editing its profile while a workflow runs on the thread pool.
    private static ModProfile Snapshot(ModProfile profile)
    {
        return new ModProfile
        {
            EnabledMods = profile.EnabledMods.ToList(),
            LoadOrder = profile.LoadOrder.ToList(),
        };
    }

    private static OperationResult Deploy(GameSession session, string profileName, ModProfile profile)
    {
        // The gate keeps installs and removals out while the deploy runs, so the mods hashed here are
        // the mods the deploy reads.
        string planHash = DeploymentFingerprint.Compute(session.Workspace, profile);
        OperationResult result = session.Engine.RebuildGame(profile);
        if (result.Success)
        {
            RecordDeployedState(session, profileName, profile, planHash, result);
        }

        return result;
    }

    private static void RecordDeployedState(
        GameSession session,
        string profileName,
        ModProfile profile,
        string planHash,
        OperationResult result)
    {
        var store = new DeployedStateStore(session.Workspace);
        var state = new DeployedState(
            DeployedState.CurrentSchemaVersion,
            profileName,
            profile.EnabledMods.ToArray(),
            planHash,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            null);
        try
        {
            store.Write(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The deploy is committed and stays. The old record no longer describes the game folder,
            // so it goes, and the state reads as unknown until a deploy records it again.
            result.AddWarning(
                DiagnosticCodes.WorkspaceDeployedStateWriteFailed,
                $"The deployed state could not be recorded: {ex.Message}",
                session.Workspace.DeployedState);
            TryForgetDeployedState(store);
        }
    }

    private static void TryForgetDeployedState(DeployedStateStore store)
    {
        try
        {
            store.Forget();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The warning above already tells the player the record is wrong; this is the log line.
            Trace.TraceWarning($"An outdated deployed.json could not be removed: {ex.Message}");
        }
    }
}
