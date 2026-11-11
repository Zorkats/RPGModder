using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using RPGModder.Core.Workspace;

namespace RPGModder.Core.Application;

/// <summary>
/// Installs and removes mods in the workspace library under the session's gate.
/// </summary>
public sealed class ModLibraryWorkflow
{
    private const string InstallOperation = "Install mod";
    private const string RemoveOperation = "Remove mod";

    private readonly ModInstallerService _installer;
    private readonly IProfileStore? _profiles;
    private readonly TimeProvider _clock;

    /// <summary>Uses a profile store on the session's file operations.</summary>
    public ModLibraryWorkflow()
    {
        _installer = new ModInstallerService();
        _clock = TimeProvider.System;
    }

    public ModLibraryWorkflow(ModInstallerService installer, IProfileStore profiles, TimeProvider clock)
    {
        _installer = installer;
        _profiles = profiles;
        _clock = clock;
    }

    public Task<InstallOutcome> InstallAsync(
        GameSession session,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        return RunInstallAsync(session, store => _installer.InstallMod(sourcePath, store), cancellationToken);
    }

    public Task<InstallOutcome> InstallFromNexusAsync(
        GameSession session,
        string archivePath,
        int nexusId,
        string version,
        string? existingFolderName,
        CancellationToken cancellationToken = default)
    {
        return RunInstallAsync(
            session,
            store => _installer.InstallModWithNexusInfo(archivePath, store, nexusId, version, existingFolderName),
            cancellationToken);
    }

    /// <summary>
    /// Moves the mod folder into the workspace trash, takes the mod out of every profile, then purges
    /// the trash entry. A failure leaves either the old state or the folder in the trash, never a mod
    /// that is half deleted.
    /// </summary>
    public async Task<OperationResult> RemoveAsync(
        GameSession session,
        string modFolderName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, RemoveOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return refusal;
        }

        using (lease)
        {
            return await Task.Run(() => Remove(session, modFolderName), CancellationToken.None);
        }
    }

    private async Task<InstallOutcome> RunInstallAsync(
        GameSession session,
        Func<ModStore, InstallResult> install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, InstallOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return new InstallOutcome(refusal, null, null);
        }

        using (lease)
        {
            return await Task.Run(() => Install(session, install), CancellationToken.None);
        }
    }

    private static InstallOutcome Install(GameSession session, Func<ModStore, InstallResult> install)
    {
        var result = new OperationResult();
        InstallResult installed = install(ModsOf(session));
        if (!installed.Success || installed.FolderName == null)
        {
            result.AddError(DiagnosticCodes.InstallFailed, installed.Error ?? "The mod could not be installed.");
            return new InstallOutcome(result, null, null);
        }

        return new InstallOutcome(result, installed.FolderName, installed.Manifest);
    }

    private OperationResult Remove(GameSession session, string modFolderName)
    {
        var result = new OperationResult();
        ModStore mods = ModsOf(session);
        string trashEntry;
        try
        {
            string folderName = SafePathService.ValidateDirectoryName(modFolderName, "Mod folder");
            trashEntry = mods.MoveToTrash(folderName, session.Workspace.Trash, _clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.InstallRemoveFailed, ex.Message, modFolderName);
            return result;
        }

        try
        {
            RemoveFromProfiles(session, modFolderName, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            result.AddError(
                DiagnosticCodes.InstallRemoveFailed,
                $"The mod folder was moved to the trash, but the profiles could not be updated: {ex.Message}",
                trashEntry);
            return result;
        }

        PurgeTrashEntry(mods, trashEntry, result);
        return result;
    }

    private void RemoveFromProfiles(GameSession session, string modFolderName, OperationResult result)
    {
        GameWorkspacePaths workspace = session.Workspace;
        IProfileStore profiles = _profiles ?? new ProfileStore(session.Files);
        foreach (string profileName in profiles.List(workspace))
        {
            if (!File.Exists(workspace.GetProfilePath(profileName)))
            {
                continue;
            }

            ModProfile profile = profiles.Load(workspace, profileName);
            int removed = profile.EnabledMods.RemoveAll(name => name.Equals(modFolderName, StringComparison.OrdinalIgnoreCase))
                          + profile.LoadOrder.RemoveAll(name => name.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                continue;
            }

            if (!profiles.CanWrite(workspace, profileName, profile))
            {
                // GR-V2: a profile from a newer build keeps its list; it names a mod that is gone.
                result.AddWarning(
                    DiagnosticCodes.ProfileSchemaUnsupported,
                    "The profile was written by a newer version of RPGModder and still lists the removed mod.",
                    profileName);
                continue;
            }

            profiles.Save(workspace, profileName, profile);
        }
    }

    private static ModStore ModsOf(GameSession session)
    {
        return new ModStore(session.Workspace.Mods, session.Files);
    }

    private static void PurgeTrashEntry(ModStore mods, string trashEntry, OperationResult result)
    {
        try
        {
            mods.PurgeTrashEntry(trashEntry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddWarning(
                DiagnosticCodes.InstallTrashNotPurged,
                $"The removed mod is kept in the trash: {ex.Message}",
                trashEntry);
        }
    }
}
