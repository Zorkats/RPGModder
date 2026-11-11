using System.Globalization;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

/// <summary>
/// Installs and removes mods in the workspace library under the session's gate.
/// </summary>
public sealed class ModLibraryWorkflow
{
    private const string InstallOperation = "Install mod";
    private const string RemoveOperation = "Remove mod";

    private readonly ModInstallerService _installer;
    private readonly IProfileRepository _profiles;
    private readonly TimeProvider _clock;

    public ModLibraryWorkflow()
        : this(new ModInstallerService(), new ProfileRepository(), TimeProvider.System)
    {
    }

    public ModLibraryWorkflow(ModInstallerService installer, IProfileRepository profiles, TimeProvider clock)
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
        return RunInstallAsync(session, modsDirectory => _installer.InstallMod(sourcePath, modsDirectory), cancellationToken);
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
            modsDirectory => _installer.InstallModWithNexusInfo(archivePath, modsDirectory, nexusId, version, existingFolderName),
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
            return await Task.Run(() => Remove(session.Workspace, modFolderName), CancellationToken.None);
        }
    }

    private async Task<InstallOutcome> RunInstallAsync(
        GameSession session,
        Func<string, InstallResult> install,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkflowStart.TryBegin(session, InstallOperation, false, out IDisposable? lease, out OperationResult refusal))
        {
            return new InstallOutcome(refusal, null, null);
        }

        using (lease)
        {
            return await Task.Run(() => Install(session.Workspace, install), CancellationToken.None);
        }
    }

    private static InstallOutcome Install(GameWorkspacePaths workspace, Func<string, InstallResult> install)
    {
        var result = new OperationResult();
        Directory.CreateDirectory(workspace.Mods);
        InstallResult installed = install(workspace.Mods);
        if (!installed.Success || installed.FolderName == null)
        {
            result.AddError(DiagnosticCodes.InstallFailed, installed.Error ?? "The mod could not be installed.");
            return new InstallOutcome(result, null, null);
        }

        return new InstallOutcome(result, installed.FolderName, installed.Manifest);
    }

    private OperationResult Remove(GameWorkspacePaths workspace, string modFolderName)
    {
        var result = new OperationResult();
        string modPath;
        string trashEntry;
        try
        {
            string folderName = SafePathService.ValidateDirectoryName(modFolderName, "Mod folder");
            modPath = SafePathService.ResolveContainedPath(workspace.Mods, folderName, "Mod folder");
            trashEntry = MoveToTrash(workspace, folderName, modPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.AddError(DiagnosticCodes.InstallRemoveFailed, ex.Message, modFolderName);
            return result;
        }

        try
        {
            RemoveFromProfiles(workspace, modFolderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            result.AddError(
                DiagnosticCodes.InstallRemoveFailed,
                $"The mod folder was moved to the trash, but the profiles could not be updated: {ex.Message}",
                trashEntry);
            return result;
        }

        PurgeTrashEntry(trashEntry, result);
        return result;
    }

    private string MoveToTrash(GameWorkspacePaths workspace, string folderName, string modPath)
    {
        if (!Directory.Exists(modPath))
        {
            throw new DirectoryNotFoundException($"The mod folder does not exist: {folderName}");
        }

        Directory.CreateDirectory(workspace.Trash);
        string stamp = _clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string trashEntry = Path.Combine(workspace.Trash, $"{folderName}-{stamp}");
        for (int suffix = 1; Directory.Exists(trashEntry) || File.Exists(trashEntry); suffix++)
        {
            trashEntry = Path.Combine(workspace.Trash, $"{folderName}-{stamp}-{suffix}");
        }

        Directory.Move(modPath, trashEntry);
        return trashEntry;
    }

    private void RemoveFromProfiles(GameWorkspacePaths workspace, string modFolderName)
    {
        foreach (string profileName in _profiles.List(workspace))
        {
            if (!File.Exists(workspace.GetProfilePath(profileName)))
            {
                continue;
            }

            ModProfile profile = _profiles.Load(workspace, profileName);
            int removed = profile.EnabledMods.RemoveAll(name => name.Equals(modFolderName, StringComparison.OrdinalIgnoreCase))
                          + profile.LoadOrder.RemoveAll(name => name.Equals(modFolderName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _profiles.Save(workspace, profileName, profile);
            }
        }
    }

    private static void PurgeTrashEntry(string trashEntry, OperationResult result)
    {
        try
        {
            Directory.Delete(trashEntry, true);
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
