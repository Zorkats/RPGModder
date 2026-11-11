using Newtonsoft.Json;
using RPGModder.Core.Platform;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Imports the layout of RPGModder 1.x (folders next to the game) into <c>.rpgmodder</c> once, and
/// records the workspace schema version. Files already in the workspace are never overwritten.
/// </summary>
public sealed class WorkspaceMigrationService
{
    private readonly IFileSystemOps _files;

    public WorkspaceMigrationService()
        : this(new FileSystemOps())
    {
    }

    public WorkspaceMigrationService(IFileSystemOps files)
    {
        _files = files;
    }

    public WorkspaceMigrationResult ImportLegacyLayout(GameWorkspacePaths paths)
    {
        paths.EnsureCreated();
        var result = new WorkspaceMigrationResult();
        int existingSchemaVersion = ReadSchemaVersion(paths);
        if (existingSchemaVersion >= GameWorkspacePaths.LegacyImportSchemaVersion)
        {
            return result;
        }

        result.ImportedMods = CountMissingTopLevelDirectories(paths.LegacyMods, paths.Mods);
        result.ImportedFiles += ImportDirectoryMissing(paths.LegacyMods, paths.Mods);
        result.ImportedFiles += ImportDirectoryMissing(paths.LegacyCleanBackup, paths.CleanBackup);
        result.ImportedFiles += ImportDirectoryMissing(paths.LegacySaveBackups, paths.SaveBackups);
        result.ImportedFiles += ImportDirectoryMissing(paths.LegacyProfileSaves, paths.ProfileSaves);

        if (ImportFileMissing(paths.LegacyDefaultProfile, paths.GetProfilePath("Default")))
        {
            result.ImportedProfiles++;
        }

        foreach (string legacyProfile in paths.LegacyNamedProfiles())
        {
            string name = Path.GetFileNameWithoutExtension(legacyProfile)[GameWorkspacePaths.LegacyProfilePrefix.Length..];
            try
            {
                if (ImportFileMissing(legacyProfile, paths.GetProfilePath(name)))
                {
                    result.ImportedProfiles++;
                }
            }
            catch (InvalidDataException ex)
            {
                result.Warnings.Add($"Skipped legacy profile '{name}': {ex.Message}");
            }
        }

        ImportFileMissing(paths.LegacyActiveProfileMarker, paths.ActiveProfileMarker);

        WriteSchemaVersion(paths, GameWorkspacePaths.LegacyImportSchemaVersion, DateTimeOffset.UtcNow);
        result.SchemaUpgraded = true;
        return result;
    }

    /// <summary>Records the workspace schema version in <c>workspace.json</c>, atomically.</summary>
    public void WriteSchemaVersion(GameWorkspacePaths paths, int version, DateTimeOffset utcNow)
    {
        var marker = new WorkspaceSchemaMarker
        {
            SchemaVersion = version,
            MigratedUtc = utcNow.UtcDateTime
        };
        _files.WriteAllTextAtomic(paths.SchemaMarker, JsonConvert.SerializeObject(marker, Formatting.Indented));
    }

    /// <summary>The workspace schema version in <c>workspace.json</c>; 0 when the file is missing or unreadable.</summary>
    public static int ReadSchemaVersion(GameWorkspacePaths paths)
    {
        string markerPath = paths.SchemaMarker;
        if (!File.Exists(markerPath))
        {
            return 0;
        }

        try
        {
            WorkspaceSchemaMarker? marker = JsonConvert.DeserializeObject<WorkspaceSchemaMarker>(File.ReadAllText(markerPath));
            return marker?.SchemaVersion ?? 0;
        }
        catch (JsonException ex)
        {
            // An unreadable marker records no version, so the import runs again. It never overwrites.
            System.Diagnostics.Trace.TraceWarning($"workspace.json is not valid JSON and the legacy import runs again: {ex.Message}");
            return 0;
        }
    }

    private int ImportDirectoryMissing(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return 0;
        }

        Directory.CreateDirectory(destination);
        int importedFiles = 0;
        foreach (string file in Directory.GetFiles(source))
        {
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string target = Path.Combine(destination, fileInfo.Name);
            if (!File.Exists(target))
            {
                _files.CopyFile(file, target);
                importedFiles++;
            }
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            importedFiles += ImportDirectoryMissing(directory, Path.Combine(destination, directoryInfo.Name));
        }

        return importedFiles;
    }

    private bool ImportFileMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return false;
        }

        _files.CopyFile(source, destination);
        return true;
    }

    private static int CountMissingTopLevelDirectories(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return 0;
        }

        return Directory.GetDirectories(source)
            .Count(directory => !Directory.Exists(Path.Combine(destination, Path.GetFileName(directory))));
    }

    private sealed class WorkspaceSchemaMarker
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("migratedUtc")]
        public DateTime MigratedUtc { get; set; }
    }
}
