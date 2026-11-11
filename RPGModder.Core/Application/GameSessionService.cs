using RPGModder.Core.Deployment;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using RPGModder.Core.Workspace;
using System.Diagnostics;
using System.Globalization;

namespace RPGModder.Core.Application;

public sealed record GameSession(
    string ExecutablePath,
    string GameRoot,
    GameWorkspacePaths Workspace,
    ModEngine Engine,
    OperationGate Gate,
    IGameProcessProbe GameProcess,
    GameLayout Layout,
    IFileSystemOps Files,
    IStoreManifestSource Store,
    TimeProvider Clock);

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
    private readonly IFileSystemOps _files;
    private readonly IStoreManifestSource _store;
    private readonly TimeProvider _clock;

    public GameSessionService()
        : this(new GameProcessProbe())
    {
    }

    public GameSessionService(IGameProcessProbe gameProcess)
        : this(gameProcess, new FileSystemOps())
    {
    }

    public GameSessionService(IGameProcessProbe gameProcess, IFileSystemOps files)
        : this(gameProcess, files, SteamStore(files), TimeProvider.System)
    {
    }

    public GameSessionService(IGameProcessProbe gameProcess, IFileSystemOps files, IStoreManifestSource store, TimeProvider clock)
    {
        _gameProcess = gameProcess;
        _files = files;
        _store = store;
        _clock = clock;
    }

    /// <summary>The store source of a real session: Steam's own manifests, read only, and Steam's link handler.</summary>
    public static IStoreManifestSource SteamStore(IFileSystemOps files)
    {
        return new SteamDepotManifestReader(SteamLibraryLocator.GetSteamRoots, files, new SteamLaunchService());
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
                // The one workspace and the one layout of this game (OWN-1, OWN-2), passed to everything below.
                GameLayout layout = GameLayout.Detect(gameRoot);
                var engine = new ModEngine(fullExecutablePath, workspace, layout, _files, _store, _clock);
                engine.InitializeSafeState();
                return new GameSession(fullExecutablePath, gameRoot, workspace, engine, gate, _gameProcess, layout, _files, _store, _clock);
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
        return profile.Clone();
    }

    private static OperationResult Deploy(GameSession session, string profileName, ModProfile profile)
    {
        OperationResult baseline = CheckBaseline(session);
        if (!baseline.Success)
        {
            return baseline;
        }

        // The gate keeps installs and removals out while the deploy runs, so the mods hashed here are
        // the mods the deploy reads.
        string planHash = DeploymentFingerprint.Compute(session.Workspace, profile);
        OperationResult result = session.Engine.RebuildGame(profile);
        foreach (OperationDiagnostic diagnostic in baseline.Diagnostics)
        {
            result.Add(diagnostic);
        }

        if (result.Success)
        {
            RecordDeployedState(session, profileName, profile, planHash, result);
        }

        return result;
    }

    /// <summary>
    /// GR-V6 and TX-24: no deploy runs without a backup, or with an unverified one the player has not
    /// chosen to keep. A kept or player-modified base lets the deploy run with a warning.
    /// </summary>
    public static OperationResult CheckBaseline(GameSession session)
    {
        var result = new OperationResult();
        VanillaManifest? manifest;
        try
        {
            manifest = new VanillaStore(session.Workspace, session.Layout, session.Files).ReadManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                $"The vanilla backup's manifest cannot be read, so the backup is not trusted. Rebuild it: {ex.Message}",
                session.Workspace.VanillaManifest);
            return result;
        }

        if (manifest == null)
        {
            result.AddError(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                "There is no vanilla backup yet. Rebuild it before deploying.",
                session.Workspace.CleanBackup);
        }
        else if (manifest.BlocksDeploy)
        {
            result.AddError(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                "The vanilla backup was migrated from an older version and could not be proven. Choose \"Rebuild\" or \"Keep this backup\".",
                AuditSubject(manifest));
        }
        else if (manifest.WarnsOnDeploy)
        {
            result.AddWarning(
                DiagnosticCodes.WorkspaceBaselineUnverified,
                manifest.Baseline == VanillaBaseline.PlayerModified
                    ? $"The vanilla backup is your own base and differs from the store's files: {manifest.BaseDifferences}."
                    : "The vanilla backup was kept without proof.",
                AuditSubject(manifest));
        }

        return result;
    }

    private static string? AuditSubject(VanillaManifest manifest)
    {
        BaselineAudit? audit = manifest.Audit;
        return audit == null
            ? null
            : $"audit: {audit.SameContent} same content, {audit.SameContentAndTime} same content and time";
    }

    private static void RecordDeployedState(
        GameSession session,
        string profileName,
        ModProfile profile,
        string planHash,
        OperationResult result)
    {
        var store = new DeployedStateStore(session.Workspace, session.Files);
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
