using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using System.Diagnostics;
using System.Globalization;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only type that writes, moves or deletes a mod folder or its <c>mod.json</c> (OWN-4).
/// Manifests are read with date parsing off (R-4), written atomically, and keep fields this build
/// does not know (GR-V5). A manifest newer than this build is never written (GR-V2).
/// </summary>
public sealed class ModStore
{
    public const string ManifestFileName = "mod.json";

    private static readonly JsonSerializerSettings ReadSettings = new() { DateParseHandling = DateParseHandling.None };

    private readonly IFileSystemOps _files;

    public ModStore(string modsRoot, IFileSystemOps files)
    {
        Root = modsRoot;
        _files = files;
    }

    public string Root { get; }

    /// <summary>Parses manifest JSON. Throws <see cref="JsonException"/> for invalid JSON and <see cref="InvalidDataException"/> for an empty file.</summary>
    public static ModManifest ParseManifest(string json)
    {
        return JsonConvert.DeserializeObject<ModManifest>(json, ReadSettings)
               ?? throw new InvalidDataException("mod.json is empty or invalid.");
    }

    public static ModManifest ReadManifestFile(string manifestPath)
    {
        return ParseManifest(File.ReadAllText(manifestPath));
    }

    public static string SerializeManifest(ModManifest manifest)
    {
        return JsonConvert.SerializeObject(manifest, Formatting.Indented);
    }

    /// <summary>The folder of an installed mod. Throws when the name is not a plain folder name.</summary>
    public string FolderOf(string folderName)
    {
        return SafePathService.ResolveContainedPath(
            Root,
            SafePathService.ValidateDirectoryName(folderName, "Mod folder"),
            "Mod folder");
    }

    public string ManifestPathOf(string folderName)
    {
        return Path.Combine(FolderOf(folderName), ManifestFileName);
    }

    public ModManifest ReadManifest(string folderName)
    {
        return ReadManifestFile(ManifestPathOf(folderName));
    }

    /// <summary>Replaces an installed mod's manifest atomically.</summary>
    public void WriteManifest(string folderName, ModManifest manifest)
    {
        RequireSupported(manifest);
        _files.WriteAllTextAtomic(ManifestPathOf(folderName), SerializeManifest(manifest));
    }

    /// <summary>
    /// Copies <paramref name="sourceFolder"/> into the library as <paramref name="folderName"/>, with
    /// <paramref name="manifest"/> as its <c>mod.json</c>. The copy is staged inside the library and
    /// moved into place; an existing mod of that name is set aside first and put back if the move
    /// fails (TX-16). Returns the installed folder.
    /// </summary>
    public string Install(string sourceFolder, string folderName, ModManifest manifest)
    {
        RequireSupported(manifest);
        Directory.CreateDirectory(Root);
        string target = SafePathService.ResolveContainedPath(Root, folderName, "Mod install target");
        string staging = SafePathService.ResolveContainedPath(Root, $".install-{Guid.NewGuid():N}", "Mod install staging path");
        string rollback = SafePathService.ResolveContainedPath(Root, $".rollback-{Guid.NewGuid():N}", "Mod rollback path");

        try
        {
            _files.CopyDirectory(sourceFolder, staging);
            _files.WriteAllTextAtomic(Path.Combine(staging, ManifestFileName), SerializeManifest(manifest));
            if (Directory.Exists(target))
            {
                _files.MoveDirectory(target, rollback);
            }

            _files.MoveDirectory(staging, target);
        }
        catch
        {
            // Put the previous install back before any cleanup, so a cleanup failure cannot leave the
            // original hidden in a dot-folder the catalog skips.
            if (!Directory.Exists(target) && Directory.Exists(rollback))
            {
                _files.MoveDirectory(rollback, target);
            }

            TryDeleteWorkFolder(staging, "install staging");
            throw;
        }

        TryDeleteWorkFolder(rollback, "previous install");
        return target;
    }

    /// <summary>
    /// Moves an installed mod to <c>trash/&lt;name&gt;-&lt;utc stamp&gt;</c> (TX-17) and returns the
    /// trash entry. Throws <see cref="DirectoryNotFoundException"/> when the mod is not installed.
    /// </summary>
    public string MoveToTrash(string folderName, string trashRoot, DateTimeOffset utcNow)
    {
        string modPath = FolderOf(folderName);
        if (!Directory.Exists(modPath))
        {
            throw new DirectoryNotFoundException($"The mod folder does not exist: {folderName}");
        }

        Directory.CreateDirectory(trashRoot);
        string stamp = utcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string entry = Path.Combine(trashRoot, $"{folderName}-{stamp}");
        for (int suffix = 1; Directory.Exists(entry) || File.Exists(entry); suffix++)
        {
            entry = Path.Combine(trashRoot, $"{folderName}-{stamp}-{suffix}");
        }

        _files.MoveDirectory(modPath, entry);
        return entry;
    }

    /// <summary>Deletes a trash entry. Throws on failure, so the caller can say the entry is kept.</summary>
    public void PurgeTrashEntry(string trashEntry)
    {
        _files.DeleteDirectory(trashEntry);
    }

    private static void RequireSupported(ModManifest manifest)
    {
        if (!manifest.IsSupported)
        {
            throw new InvalidDataException(
                $"mod.json declares schema version {manifest.EffectiveSchemaVersion}, which is newer than this version of RPGModder supports.");
        }
    }

    private void TryDeleteWorkFolder(string folder, string purpose)
    {
        try
        {
            _files.DeleteDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A dot-folder is skipped by the catalog and holds a copy only; the install result stands.
            Trace.TraceWarning($"The {purpose} folder could not be removed and is left in place: {folder}: {ex.Message}");
        }
    }
}
