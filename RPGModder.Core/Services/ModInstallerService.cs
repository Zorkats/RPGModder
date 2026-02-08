using Newtonsoft.Json;
using RPGModder.Core.Models;
using System.IO.Compression;

namespace RPGModder.Core.Services;

// Handles mod installation from folders and ZIP files
public class ModInstallerService
{

    public InstallResult InstallModWithNexusInfo(
        string sourcePath,
        string modsDirectory,
        int nexusId,
        string version,
        string existingFolderName = null)
    {
        // 1. Install normally first to unpack everything
        var result = InstallMod(sourcePath, modsDirectory, existingFolderName);

        if (result.Success && result.Manifest != null && !string.IsNullOrEmpty(result.FolderName))
        {
            // 2. Inject Nexus Metadata
            result.Manifest.Metadata.NexusId = nexusId;
            // Only update version if the mod doesn't have its own specific version string
            if (string.IsNullOrEmpty(result.Manifest.Metadata.Version) || result.Manifest.Metadata.Version == "1.0")
            {
                result.Manifest.Metadata.Version = version;
            }

            // 3. Save the updated mod.json back to disk
            string installedPath = Path.Combine(modsDirectory, result.FolderName, "mod.json");
            File.WriteAllText(installedPath,
                JsonConvert.SerializeObject(result.Manifest, Formatting.Indented));
        }

        return result;
    }

    // Installs a mod from a folder or ZIP file to the target mods directory
    // Returns the installed mod's folder name, or null if installation failed
    public InstallResult InstallMod(string sourcePath, string modsDirectory, string? targetFolderName = null)
    {
        if (File.Exists(sourcePath))
        {
            // Check by extension first, then by magic bytes (handles missing/wrong extensions)
            if (IsZipFile(sourcePath))
            {
                return InstallFromZip(sourcePath, modsDirectory, targetFolderName);
            }

            return new InstallResult
            {
                Success = false,
                Error = "Unsupported file format. Only ZIP archives and folders with mod.json are supported."
            };
        }
        else if (Directory.Exists(sourcePath))
        {
            return InstallFromFolder(sourcePath, modsDirectory, targetFolderName);
        }

        return new InstallResult { Success = false, Error = "File or folder not found." };
    }

    // Checks if a file is a ZIP archive by extension or by reading magic bytes (PK\x03\x04)
    private static bool IsZipFile(string filePath)
    {
        if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check ZIP magic bytes: PK\x03\x04
        try
        {
            using var stream = File.OpenRead(filePath);
            var header = new byte[4];
            if (stream.Read(header, 0, 4) == 4)
            {
                return header[0] == 0x50 && header[1] == 0x4B &&
                       header[2] == 0x03 && header[3] == 0x04;
            }
        }
        catch { }

        return false;
    }

    // Installs from a ZIP file - extracts and finds mod.json
    private InstallResult InstallFromZip(string zipPath, string modsDirectory, string? targetFolderName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"RPGModder_Extract_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            // Find mod.json recursively
            var modJsonPath = FindModJson(tempDir);
            if (modJsonPath == null)
            {
                return new InstallResult { Success = false, Error = "No mod.json found in ZIP file." };
            }

            // The mod root is the folder containing mod.json
            string modRoot = Path.GetDirectoryName(modJsonPath)!;
            return InstallFromFolder(modRoot, modsDirectory, targetFolderName);
        }
        catch (Exception ex)
        {
            return new InstallResult { Success = false, Error = $"Failed to extract ZIP: {ex.Message}" };
        }
        finally
        {
            // Cleanup temp directory
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    // Installs from a folder - copies to mods directory
    private InstallResult InstallFromFolder(string folderPath, string modsDirectory, string? targetFolderName)
    {
        // First check if mod.json exists directly in the folder
        string directModJson = Path.Combine(folderPath, "mod.json");
        string modJsonPath;

        if (File.Exists(directModJson))
        {
            modJsonPath = directModJson;
        }
        else
        {
            // Search recursively for mod.json
            var foundPath = FindModJson(folderPath);
            if (foundPath == null)
            {
                return new InstallResult { Success = false, Error = "No mod.json found in folder or its subdirectories." };
            }
            modJsonPath = foundPath;
            // Update folderPath to be the actual mod root
            folderPath = Path.GetDirectoryName(modJsonPath)!;
        }

        try
        {
            var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(modJsonPath));
            if (manifest == null) return new InstallResult { Success = false, Error = "Failed to parse mod.json" };

            // Determine folder name
            string folderName;

            if (!string.IsNullOrEmpty(targetFolderName))
            {
                // Use the existing folder name
                folderName = targetFolderName;
            }
            else
            {
                // NEW INSTALL: Use ID or Folder Name
                folderName = !string.IsNullOrWhiteSpace(manifest.Metadata.Id)
                    ? manifest.Metadata.Id
                    : new DirectoryInfo(folderPath).Name;

                if (string.IsNullOrWhiteSpace(folderName)) folderName = $"Mod_{DateTime.Now.Ticks}";
            }

            string targetPath = Path.Combine(modsDirectory, folderName);

            // Nuke existing folder (Update logic)
            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, true);

            CopyDirectory(folderPath, targetPath);

            return new InstallResult
            {
                Success = true,
                FolderName = folderName,
                Manifest = manifest
            };
        }
        catch (Exception ex)
        {
            return new InstallResult { Success = false, Error = ex.Message };
        }
    }

    // Recursively searches for mod.json in a directory
    public string? FindModJson(string rootPath)
    {
        // Check root first
        string rootModJson = Path.Combine(rootPath, "mod.json");
        if (File.Exists(rootModJson))
            return rootModJson;

        // Search subdirectories (breadth-first, prioritize shallow)
        try
        {
            var directories = Directory.GetDirectories(rootPath);
            
            // First pass: check immediate children
            foreach (var dir in directories)
            {
                string childModJson = Path.Combine(dir, "mod.json");
                if (File.Exists(childModJson))
                    return childModJson;
            }

            // Second pass: recurse deeper
            foreach (var dir in directories)
            {
                var found = FindModJson(dir);
                if (found != null)
                    return found;
            }
        }
        catch { }

        return null;
    }

    // Validates a path contains a valid mod structure
    public ValidationResult ValidateMod(string path)
    {
        string? modJsonPath = null;

        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // For ZIP, we'd need to peek inside - for now just check it exists
            return new ValidationResult { IsValid = true, ModJsonPath = null };
        }
        else if (Directory.Exists(path))
        {
            modJsonPath = FindModJson(path);
        }

        if (modJsonPath == null)
        {
            return new ValidationResult { IsValid = false, Error = "No mod.json found." };
        }

        try
        {
            var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(modJsonPath));
            if (manifest == null)
            {
                return new ValidationResult { IsValid = false, Error = "Invalid mod.json format." };
            }

            return new ValidationResult
            {
                IsValid = true,
                ModJsonPath = modJsonPath,
                Manifest = manifest
            };
        }
        catch (Exception ex)
        {
            return new ValidationResult { IsValid = false, Error = $"Failed to parse mod.json: {ex.Message}" };
        }
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }
}

public class InstallResult
{
    public bool Success { get; set; }
    public string? FolderName { get; set; }
    public ModManifest? Manifest { get; set; }
    public string? Error { get; set; }
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public string? ModJsonPath { get; set; }
    public ModManifest? Manifest { get; set; }
    public string? Error { get; set; }
}
