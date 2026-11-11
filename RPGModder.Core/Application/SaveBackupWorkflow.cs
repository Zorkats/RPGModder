using System.Globalization;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;

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
            string save = RequireInside(session.GameRoot, saveFilePath, "Save file");
            Directory.CreateDirectory(session.Workspace.SaveBackups);
            File.Copy(save, BackupPath(session, save, string.Empty), true);
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
            string save = RequireInside(session.GameRoot, saveFilePath, "Save file");
            string backup = RequireInside(session.Workspace.SaveBackups, backupFilePath, "Save backup");
            if (File.Exists(save))
            {
                File.Copy(save, BackupPath(session, save, "_PRE_RESTORE"), true);
            }

            File.Copy(backup, save, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.SaveRestoreFailed, ex.Message, Path.GetFileName(saveFilePath));
        }

        return result;
    }

    private string BackupPath(GameSession session, string saveFile, string suffix)
    {
        string stamp = _clock.GetLocalNow().ToString("MMdd_HHmm", CultureInfo.InvariantCulture);
        return Path.Combine(session.Workspace.SaveBackups, $"{Path.GetFileName(saveFile)}{suffix}_{stamp}.bak");
    }

    private static string RequireInside(string root, string path, string fieldName)
    {
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return SafePathService.ResolveContainedPath(root, relative, fieldName);
    }
}
