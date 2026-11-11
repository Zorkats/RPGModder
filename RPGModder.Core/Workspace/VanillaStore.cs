using System.Security.Cryptography;
using Newtonsoft.Json;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only writer of <c>.rpgmodder/backups/vanilla</c> (OWN-3). Until A5 replaces it with
/// <c>vanilla.manifest.json</c>, a backup is complete when its marker file exists; a folder without
/// the marker is a partial backup and is never trusted.
/// </summary>
public sealed class VanillaStore
{
    public const string MarkerFileName = "backup_complete.marker";

    private readonly GameLayout _layout;
    private readonly IFileSystemOps _files;

    public VanillaStore(GameWorkspacePaths workspace, GameLayout layout, IFileSystemOps files)
    {
        Root = workspace.CleanBackup;
        _layout = layout;
        _files = files;
    }

    public string Root { get; }

    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    public bool IsComplete => File.Exists(MarkerPath);

    /// <summary>
    /// Takes the backup from the game folder unless a complete one exists. A partial backup is
    /// thrown away first. Throws <see cref="InvalidOperationException"/> when a runtime loader has
    /// already patched the game, because then the folder is not vanilla.
    /// </summary>
    public void EnsureBackup()
    {
        if (IsComplete)
        {
            return;
        }

        DiscardPartialBackup();
        if (IsDirtyInstallation())
        {
            throw new InvalidOperationException(
                "Dirty installation detected! It looks like Fear & Hunger Mod Manager (FHMM) or another runtime loader " +
                "is already installed directly into your base game.\n\n" +
                "To use RPGModder safely, you must have a 100% clean, unmodified game. " +
                "Please verify your game files on Steam/Itch.io to restore the vanilla state, then install FHMM as a standard ZIP mod inside RPGModder.");
        }

        CopyManagedContent();
        WriteMarker();
    }

    /// <summary>
    /// Removes the marker, then the folder. The marker goes first, so an interruption at any point
    /// leaves no trusted backup and the next open takes a fresh one.
    /// </summary>
    public void Discard()
    {
        _files.DeleteFile(MarkerPath);
        _files.DeleteDirectory(Root);
    }

    public bool Contains(string relativePath)
    {
        return File.Exists(PathOf(relativePath));
    }

    /// <summary>The full path of a backup file; throws when the relative path leaves the backup.</summary>
    public string PathOf(string relativePath)
    {
        return SafePathService.ResolveContainedPath(Root, relativePath, "Backup target");
    }

    public string ReadText(string relativePath)
    {
        return File.ReadAllText(PathOf(relativePath));
    }

    /// <summary>The SHA-256 of a backup file, as upper-case hex.</summary>
    public string Hash(string relativePath)
    {
        using FileStream stream = File.OpenRead(PathOf(relativePath));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Every backed-up file, relative to the content root, with forward slashes. The marker is not listed.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            if (!Directory.Exists(Root))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/'))
                .Where(relative => !relative.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(relative => relative, StringComparer.Ordinal)
                .ToList();
        }
    }

    private void DiscardPartialBackup()
    {
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any())
        {
            _files.DeleteDirectory(Root);
        }

        Directory.CreateDirectory(Root);
    }

    private bool IsDirtyInstallation()
    {
        // Steam's "Verify Integrity" leaves orphaned folders behind, so a loader's folder proves
        // nothing. The game is dirty only when its boot page loads a runtime loader.
        string indexHtml = Path.Combine(_layout.ContentRoot, "index.html");
        if (!File.Exists(indexHtml))
        {
            return false;
        }

        string content = File.ReadAllText(indexHtml);
        return content.Contains("mattieFMModLoader.js", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("MATTIE", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("TY_ModLoader", StringComparison.OrdinalIgnoreCase);
    }

    private void CopyManagedContent()
    {
        foreach (string folder in FilePolicyService.ProtectedFolders)
        {
            string source = Path.Combine(_layout.ContentRoot, folder);
            if (Directory.Exists(source))
            {
                _files.CopyDirectory(source, Path.Combine(Root, folder));
            }
        }

        foreach (string file in FilePolicyService.ProtectedRootFiles)
        {
            string source = Path.Combine(_layout.ContentRoot, file);
            if (File.Exists(source))
            {
                _files.CopyFile(source, Path.Combine(Root, file));
            }
        }
    }

    private void WriteMarker()
    {
        string marker = JsonConvert.SerializeObject(new
        {
            BackupDate = DateTime.Now,
            UsesWwwFolder = _layout.UsesWww,
            ContentPath = _layout.ContentRoot
        }, Formatting.Indented);
        _files.WriteAllTextAtomic(MarkerPath, marker);
    }
}
