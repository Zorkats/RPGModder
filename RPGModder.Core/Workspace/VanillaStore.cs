using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using System.Security.Cryptography;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only writer of <c>.rpgmodder/backups/vanilla</c> and <c>vanilla.manifest.json</c> (OWN-3).
/// A backup is complete when its manifest exists; a folder without one is never trusted. The 2.0.0
/// marker <c>backup_complete.marker</c> is read only by the 3 to 4 migration (GR-V6).
/// </summary>
public sealed class VanillaStore
{
    public const string MarkerFileName = "backup_complete.marker";

    public const string DirtyInstallationMessage =
        "Dirty installation detected! It looks like Fear & Hunger Mod Manager (FHMM) or another runtime loader " +
        "is already installed directly into your base game.\n\n" +
        "To use RPGModder safely, you must have a 100% clean, unmodified game. " +
        "Please verify your game files on Steam/Itch.io to restore the vanilla state, then install FHMM as a standard ZIP mod inside RPGModder.";

    private readonly GameWorkspacePaths _workspace;
    private readonly GameLayout _layout;
    private readonly IFileSystemOps _files;

    public VanillaStore(GameWorkspacePaths workspace, GameLayout layout, IFileSystemOps files)
    {
        _workspace = workspace;
        Root = workspace.CleanBackup;
        _layout = layout;
        _files = files;
    }

    public string Root { get; }

    public string ManifestPath => _workspace.VanillaManifest;

    /// <summary>The 2.0.0 completion marker inside the backup folder (schema 3).</summary>
    public string MarkerPath => Path.Combine(Root, MarkerFileName);

    public bool IsComplete => File.Exists(ManifestPath);

    /// <summary>The manifest, or null when there is none. An unreadable manifest is <see cref="InvalidDataException"/>.</summary>
    public VanillaManifest? ReadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<VanillaManifest>(File.ReadAllText(ManifestPath))
                   ?? throw new InvalidDataException("vanilla.manifest.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"vanilla.manifest.json is not valid: {ex.Message}", ex);
        }
    }

    public void WriteManifest(VanillaManifest manifest)
    {
        _files.WriteAllTextAtomic(ManifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
    }

    /// <summary>Records the player's "Keep this backup" for an unverified backup (GR-V6).</summary>
    public VanillaManifest Accept(DateTimeOffset utcNow)
    {
        VanillaManifest manifest = ReadManifest() ?? throw new InvalidOperationException("There is no vanilla backup to keep.");
        if (manifest.BaselineAcceptedUtc == null)
        {
            manifest.BaselineAcceptedUtc = utcNow;
            WriteManifest(manifest);
        }

        return manifest;
    }

    /// <summary>
    /// Removes the manifest, then the marker, then the folders. The manifest goes first, so an
    /// interruption at any point leaves no trusted backup and the next open takes a fresh one.
    /// </summary>
    public void Discard()
    {
        _files.DeleteFile(ManifestPath);
        _files.DeleteFile(MarkerPath);
        _files.DeleteDirectory(Root);
        _files.DeleteDirectory(_workspace.VanillaStaging);
        _files.DeleteDirectory(_workspace.VanillaRetired);
    }

    /// <summary>
    /// Builds a new backup from <paramref name="sources"/> (TX-6, TX-23): the old manifest is deleted,
    /// each file is copied into a staging folder and hashed again after the copy (and checked against
    /// the store's SHA-1 when one is given), the folders are swapped, and the new manifest is written
    /// last. An interruption at any step leaves no manifest, so no partial backup is ever trusted.
    /// </summary>
    public void Replace(IReadOnlyList<BackupSource> sources, VanillaManifest manifest)
    {
        _files.DeleteFile(ManifestPath);
        _files.DeleteDirectory(_workspace.VanillaStaging);
        _files.DeleteDirectory(_workspace.VanillaRetired);

        var entries = new List<VanillaFileEntry>();
        foreach (BackupSource source in sources.OrderBy(source => source.RelativePath, StringComparer.Ordinal))
        {
            string staged = SafePathService.ResolveContainedPath(_workspace.VanillaStaging, source.RelativePath, "Backup target");
            _files.CopyFile(source.SourcePath, staged);
            byte[] bytes = File.ReadAllBytes(staged);
            if (source.StoreSha1 != null && Convert.ToHexString(SHA1.HashData(bytes)) != source.StoreSha1)
            {
                throw new InvalidDataException($"The copy of {source.RelativePath} does not equal the store's file.");
            }

            entries.Add(new VanillaFileEntry
            {
                Path = source.RelativePath,
                Size = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
        }

        Directory.CreateDirectory(_workspace.VanillaStaging);
        _files.DeleteFile(MarkerPath);
        if (Directory.Exists(Root))
        {
            _files.MoveDirectory(Root, _workspace.VanillaRetired);
        }

        _files.MoveDirectory(_workspace.VanillaStaging, Root);
        _files.DeleteDirectory(_workspace.VanillaRetired);

        manifest.Files = entries;
        WriteManifest(manifest);
    }

    /// <summary>
    /// Undoes a swap that stopped between moving the backup aside and moving the new one in, so the
    /// old files are where the next build looks for proven copies. Nothing is trusted without a manifest.
    /// </summary>
    public void RecoverInterruptedSwap()
    {
        if (!Directory.Exists(Root) && Directory.Exists(_workspace.VanillaRetired))
        {
            _files.MoveDirectory(_workspace.VanillaRetired, Root);
        }
    }

    /// <summary>Hashes the backup as it stands into manifest entries, for the migration (GR-V6).</summary>
    public List<VanillaFileEntry> Describe(IEnumerable<string> relativeFiles)
    {
        return relativeFiles
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file =>
            {
                string path = PathOf(file);
                return new VanillaFileEntry { Path = file, Size = new FileInfo(path).Length, Sha256 = Sha256Of(path) };
            })
            .ToList();
    }

    /// <summary>Moves a backup entry (file or folder) under <paramref name="destinationRoot"/>, keeping its relative path. Nothing is deleted.</summary>
    public string MoveAside(string relativePath, string destinationRoot)
    {
        string source = PathOf(relativePath);
        string destination = SafePathService.ResolveContainedPath(destinationRoot, relativePath, "Moved backup entry");
        for (int attempt = 2; File.Exists(destination) || Directory.Exists(destination); attempt++)
        {
            destination = SafePathService.ResolveContainedPath(destinationRoot, $"{relativePath}.{attempt}", "Moved backup entry");
        }

        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            _files.MoveDirectory(source, destination);
        }
        else
        {
            _files.MoveFile(source, destination);
        }

        return destination;
    }

    /// <summary>
    /// Whether the game's boot page loads a runtime loader, in which case the folder is not vanilla.
    /// Steam's "Verify Integrity" leaves orphaned folders behind, so a loader's folder proves nothing.
    /// </summary>
    public bool HasRuntimeLoader()
    {
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
        return Sha256Of(PathOf(relativePath));
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

    public static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha1Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
