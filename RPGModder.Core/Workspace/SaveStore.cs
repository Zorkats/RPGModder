using System.Diagnostics;
using System.Globalization;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only type that touches the live <c>save</c> folder, the per-profile save sets and the save
/// backups (OWN-6).
/// </summary>
public sealed class SaveStore
{
    private const string TimeCapsuleFolder = "original-vanilla";
    private static readonly string[] SwappedSuffixes = { ".rpgsave", ".rmmzsave", "config.rpgsave", "global.rpgsave" };

    private readonly GameWorkspacePaths _workspace;
    private readonly IFileSystemOps _files;

    public SaveStore(GameWorkspacePaths workspace, GameLayout layout, IFileSystemOps files)
    {
        _workspace = workspace;
        LiveFolder = layout.SaveFolder;
        _files = files;
    }

    public string LiveFolder { get; }

    /// <summary>
    /// Copies the live saves once into <c>backups/saves/original-vanilla</c>, the time capsule of the
    /// saves from before RPGModder managed profiles. Returns true when this call created it.
    /// </summary>
    public bool EnsureTimeCapsule()
    {
        string capsule = Path.Combine(_workspace.SaveBackups, TimeCapsuleFolder);
        if (Directory.Exists(capsule) || !Directory.Exists(LiveFolder))
        {
            return false;
        }

        Directory.CreateDirectory(capsule);
        List<string> saves = Directory.GetFiles(LiveFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(save => save.EndsWith(".rpgsave") || save.EndsWith(".rmmzsave"))
            .ToList();
        foreach (string save in saves)
        {
            _files.CopyFile(save, Path.Combine(capsule, Path.GetFileName(save)));
        }

        if (saves.Count == 0)
        {
            return false;
        }

        _files.WriteAllTextAtomic(
            Path.Combine(capsule, "readme.txt"),
            "These save files were backed up by RPGModder.\n" +
            "They represent the state of your saves before you started using Profiles.\n" +
            "Restore these if your saves ever disappear or get corrupted.");
        return true;
    }

    /// <summary>
    /// Stores the live saves under the old profile and puts the new profile's saves in their place
    /// (TX-14). Everything is copied first; the live folder changes only after that. If replacing
    /// fails, the previous set is put back; if that fails too, the rollback folder is kept and named.
    /// </summary>
    public void Swap(string oldProfileName, string newProfileName)
    {
        string storageRoot = _workspace.ProfileSaves;
        string oldStorage = ProfileSaveFolder(oldProfileName, "Old profile name", "Old profile saves");
        string newStorage = ProfileSaveFolder(newProfileName, "New profile name", "New profile saves");

        Directory.CreateDirectory(LiveFolder);
        Directory.CreateDirectory(oldStorage);
        Directory.CreateDirectory(newStorage);

        List<string> liveFiles = SwappedFiles(LiveFolder);
        string stagingDir = Path.Combine(storageRoot, $"_swap_staging_{Guid.NewGuid():N}");
        string rollbackDir = Path.Combine(storageRoot, $"_swap_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(rollbackDir);

        bool keepRollback = false;
        try
        {
            CopyAll(liveFiles, rollbackDir);
            CopyAll(liveFiles, oldStorage);
            CopyAll(Directory.GetFiles(newStorage), stagingDir);
            keepRollback = !ReplaceLiveSaves(liveFiles, stagingDir, rollbackDir);
        }
        finally
        {
            TryDeleteWorkFolder(stagingDir);
            if (!keepRollback)
            {
                TryDeleteWorkFolder(rollbackDir);
            }
        }
    }

    /// <summary>Copies one save file into <c>backups/saves</c> and returns the backup's path.</summary>
    public string BackupFile(string saveFilePath, DateTimeOffset localNow)
    {
        string save = RequireInside(_workspace.GameRoot, saveFilePath, "Save file");
        string backup = BackupPath(save, string.Empty, localNow);
        _files.CopyFile(save, backup);
        return backup;
    }

    /// <summary>
    /// Copies a backup over a live save. The save it replaces is kept first as a pre-restore copy.
    /// </summary>
    public void RestoreFile(string saveFilePath, string backupFilePath, DateTimeOffset localNow)
    {
        string save = RequireInside(_workspace.GameRoot, saveFilePath, "Save file");
        string backup = RequireInside(_workspace.SaveBackups, backupFilePath, "Save backup");
        if (File.Exists(save))
        {
            _files.CopyFile(save, BackupPath(save, "_PRE_RESTORE", localNow));
        }

        _files.CopyFile(backup, save);
    }

    private string ProfileSaveFolder(string profileName, string nameField, string pathField)
    {
        return SafePathService.ResolveContainedPath(
            _workspace.ProfileSaves,
            SafePathService.ValidateDirectoryName(profileName, nameField),
            pathField);
    }

    private static List<string> SwappedFiles(string folder)
    {
        return Directory.GetFiles(folder)
            .Where(file => SwappedSuffixes.Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void CopyAll(IEnumerable<string> files, string destinationFolder)
    {
        foreach (string file in files)
        {
            _files.CopyFile(file, Path.Combine(destinationFolder, Path.GetFileName(file)));
        }
    }

    /// <summary>Returns false when the replace failed and the previous saves could not be put back.</summary>
    private bool ReplaceLiveSaves(List<string> liveFiles, string stagingDir, string rollbackDir)
    {
        try
        {
            foreach (string file in liveFiles)
            {
                _files.DeleteFile(file);
            }

            foreach (string file in Directory.GetFiles(stagingDir))
            {
                _files.MoveFile(file, Path.Combine(LiveFolder, Path.GetFileName(file)));
            }

            return true;
        }
        catch (Exception swapError)
        {
            // Any failure here, expected or not, has to put the previous saves back before it leaves.
            if (!TryRestoreSaves(rollbackDir))
            {
                throw new IOException(
                    $"Switching saves failed and the previous saves could not be put back. They are kept in '{rollbackDir}'.",
                    swapError);
            }

            throw;
        }
    }

    private bool TryRestoreSaves(string rollbackDir)
    {
        try
        {
            foreach (string file in SwappedFiles(LiveFolder))
            {
                _files.DeleteFile(file);
            }

            CopyAll(Directory.GetFiles(rollbackDir), LiveFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The previous saves could not be put back from {rollbackDir}: {ex.Message}");
            return false;
        }
    }

    private void TryDeleteWorkFolder(string folder)
    {
        try
        {
            _files.DeleteDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The swap already finished or was rolled back; a leftover work folder under
            // profile-saves holds copies only and does not change which saves are live.
            Trace.TraceWarning($"A save swap work folder could not be removed and is left in place: {folder}: {ex.Message}");
        }
    }

    private string BackupPath(string saveFile, string suffix, DateTimeOffset localNow)
    {
        string stamp = localNow.ToString("MMdd_HHmm", CultureInfo.InvariantCulture);
        return Path.Combine(_workspace.SaveBackups, $"{Path.GetFileName(saveFile)}{suffix}_{stamp}.bak");
    }

    private static string RequireInside(string root, string path, string fieldName)
    {
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return SafePathService.ResolveContainedPath(root, relative, fieldName);
    }
}
