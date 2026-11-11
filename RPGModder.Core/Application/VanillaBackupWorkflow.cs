using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Workspace;

namespace RPGModder.Core.Application;

/// <summary>
/// Throws the vanilla backup away and takes it again from a restored game folder.
/// </summary>
public sealed class VanillaBackupWorkflow
{
    private const string ResetOperation = "Reset vanilla backup";

    /// <summary>
    /// Restores vanilla from the current backup, deletes the backup's marker, then its folder, then
    /// takes a new backup. The marker goes first, so a reset interrupted at any point leaves no
    /// trusted backup, and the next open takes a fresh one.
    /// </summary>
    public async Task<OperationResult> ResetAsync(GameSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, ResetOperation, true, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Reset(session), CancellationToken.None);
        }
    }

    private static OperationResult Reset(GameSession session)
    {
        OperationResult result = session.Engine.RebuildGame(new ModProfile());
        if (!result.Success)
        {
            return result;
        }

        try
        {
            // The game folder now holds vanilla, which no profile's record describes.
            new DeployedStateStore(session.Workspace).Forget();
            File.Delete(session.Engine.BackupMarkerPath);
            if (Directory.Exists(session.Workspace.CleanBackup))
            {
                Directory.Delete(session.Workspace.CleanBackup, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddError(
                DiagnosticCodes.WorkspaceBackupResetFailed,
                $"The old vanilla backup could not be removed completely and is no longer trusted: {ex.Message}",
                session.Workspace.CleanBackup);
            return result;
        }

        try
        {
            session.Engine.InitializeSafeState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result.AddError(
                DiagnosticCodes.WorkspaceBackupResetFailed,
                $"The new vanilla backup could not be taken and is taken again when the game is opened: {ex.Message}",
                session.Workspace.CleanBackup);
        }

        return result;
    }
}
