using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Workspace;

namespace RPGModder.Core.Application;

/// <summary>
/// Backs up one live save file into the workspace and restores the latest backup of it, under the
/// session's gate. A restore refuses while the game is running, because the game rewrites its saves.
/// </summary>
public sealed class SaveBackupWorkflow
{
    private const string BackupOperation = "Back up save";
    private const string RestoreOperation = "Restore save";

    private readonly TimeProvider _clock;

    public SaveBackupWorkflow()
        : this(TimeProvider.System)
    {
    }

    public SaveBackupWorkflow(TimeProvider clock)
    {
        _clock = clock;
    }

    public async Task<OperationResult> BackupAsync(
        GameSession session,
        string saveFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, BackupOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Backup(session, saveFilePath), CancellationToken.None);
        }
    }

    public async Task<OperationResult> RestoreAsync(
        GameSession session,
        string saveFilePath,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, RestoreOperation, true, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Restore(session, saveFilePath, backupFilePath), CancellationToken.None);
        }
    }

    private OperationResult Backup(GameSession session, string saveFilePath)
    {
        var result = new OperationResult();
        try
        {
            SavesOf(session).BackupFile(saveFilePath, _clock.GetLocalNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.SaveBackupFailed, ex.Message, Path.GetFileName(saveFilePath));
        }

        return result;
    }

    private OperationResult Restore(GameSession session, string saveFilePath, string backupFilePath)
    {
        var result = new OperationResult();
        try
        {
            SavesOf(session).RestoreFile(saveFilePath, backupFilePath, _clock.GetLocalNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.SaveRestoreFailed, ex.Message, Path.GetFileName(saveFilePath));
        }

        return result;
    }

    private static SaveStore SavesOf(GameSession session)
    {
        return new SaveStore(session.Workspace, session.Layout, session.Files);
    }
}
