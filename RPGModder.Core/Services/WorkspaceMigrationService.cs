using Newtonsoft.Json;

namespace RPGModder.Core.Services;

public sealed class WorkspaceMigrationService
{
    public WorkspaceMigrationResult ImportLegacyLayout(GameWorkspacePaths paths)
    {
        paths.EnsureCreated();
        var result = new WorkspaceMigrationResult();
        int existingSchemaVersion = ReadSchemaVersion(paths.SchemaMarker);
        if (existingSchemaVersion >= GameWorkspacePaths.CurrentSchemaVersion)
        {
            return result;
        }

        string legacyMods = Path.Combine(paths.GameRoot, "Mods");
        result.ImportedMods = CountMissingTopLevelDirectories(legacyMods, paths.Mods);
        result.ImportedFiles += ImportDirectoryMissing(legacyMods, paths.Mods);
        result.ImportedFiles += ImportDirectoryMissing(
            Path.Combine(paths.GameRoot, "ModManager_Backups", "Clean_Vanilla"),
            paths.CleanBackup);
        result.ImportedFiles += ImportDirectoryMissing(
            Path.Combine(paths.GameRoot, "ModManager_Backups", "Saves"),
            paths.SaveBackups);
        result.ImportedFiles += ImportDirectoryMissing(
            Path.Combine(paths.GameRoot, "ModManager_Backups", "ProfileSaves"),
            paths.ProfileSaves);

        if (ImportFileMissing(
                Path.Combine(paths.GameRoot, "profile.json"),
                paths.GetProfilePath("Default")))
        {
            result.ImportedProfiles++;
        }

        foreach (string legacyProfile in Directory.GetFiles(paths.GameRoot, "mod_profile_*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(legacyProfile)["mod_profile_".Length..];
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

        ImportFileMissing(
            Path.Combine(paths.GameRoot, "ModManager_Backups", "active_profile.txt"),
            paths.ActiveProfileMarker);

        var marker = new WorkspaceSchemaMarker
        {
            SchemaVersion = GameWorkspacePaths.CurrentSchemaVersion,
            MigratedUtc = DateTime.UtcNow
        };
        FileTreeService.WriteAllTextAtomic(
            paths.SchemaMarker,
            JsonConvert.SerializeObject(marker, Formatting.Indented));
        result.SchemaUpgraded = true;
        return result;
    }

    private static int ImportDirectoryMissing(string source, string destination)
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
                File.Copy(file, target);
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

            importedFiles += ImportDirectoryMissing(
                directory,
                Path.Combine(destination, directoryInfo.Name));
        }

        return importedFiles;
    }

    private static bool ImportFileMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        return true;
    }

    private static int ReadSchemaVersion(string markerPath)
    {
        if (!File.Exists(markerPath))
        {
            return 0;
        }

        try
        {
            WorkspaceSchemaMarker? marker = JsonConvert.DeserializeObject<WorkspaceSchemaMarker>(
                File.ReadAllText(markerPath));
            return marker?.SchemaVersion ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
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

public sealed class WorkspaceMigrationResult
{
    public int ImportedFiles { get; set; }
    public int ImportedMods { get; set; }
    public int ImportedProfiles { get; set; }
    public bool SchemaUpgraded { get; set; }
    public List<string> Warnings { get; } = new();
}
