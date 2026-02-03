using Newtonsoft.Json;
using RPGModder.Core.Models;
using System.IO.Compression;

namespace RPGModder.Core.Services;

// Handles mod installation from folders and ZIP files
public class ModInstallerService
{
    // Installs a mod from a folder or ZIP file to the target mods directory
    // Returns the installed mod's folder name, or null if installation failed
    public InstallResult InstallMod(string sourcePath, string modsDirectory)
    {
        if (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return InstallFromZip(sourcePath, modsDirectory);
        }
        else if (Directory.Exists(sourcePath))
        {
            return InstallFromFolder(sourcePath, modsDirectory);
        }

        return new InstallResult { Success = false, Error = "Path is neither a valid folder nor a ZIP file." };
    }

    // Installs from a ZIP file - extracts and finds mod.json
    private InstallResult InstallFromZip(string zipPath, string modsDirectory)
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
            return InstallFromFolder(modRoot, modsDirectory);
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
    private InstallResult InstallFromFolder(string folderPath, string modsDirectory)
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
            if (manifest == null)
            {
                return new InstallResult { Success = false, Error = "Failed to parse mod.json." };
            }

            // Determine target folder name
            string folderName = !string.IsNullOrWhiteSpace(manifest.Metadata.Id)
                ? manifest.Metadata.Id
                : new DirectoryInfo(folderPath).Name;

            if (string.IsNullOrWhiteSpace(folderName))
                folderName = $"Mod_{DateTime.Now.Ticks}";

            string targetPath = Path.Combine(modsDirectory, folderName);

            // Remove existing if present
            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, true);

            // Copy mod folder
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
            return new InstallResult { Success = false, Error = $"Installation failed: {ex.Message}" };
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
