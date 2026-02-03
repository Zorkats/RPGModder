using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

// The Auto-Packer service - compares modded vs vanilla folders and generates mod.json
public class ModPackerService
{
    // Files to never include in a mod package
    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "save", "saves", "www/save", "www/saves",
        ".git", ".gitignore", ".DS_Store", "Thumbs.db",
        "ModManager_Backups", "Mods", "profile.json"
    };

    // Binary file extensions (not JSON-diffable)
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".ogg", ".m4a", ".mp3", ".wav", ".rpgmvo", ".rpgmvm",
        ".ttf", ".otf", ".woff", ".woff2",
        ".exe", ".dll", ".so", ".dylib"
    };

    // JSON files that support smart patching
    private static readonly HashSet<string> PatchableJsonFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "system.json"
    };

    // Analyzes differences between work and vanilla folders
    public PackerResult AnalyzeDifferences(string workFolder, string vanillaFolder)
    {
        var result = new PackerResult();

        try
        {
            // Normalize paths
            workFolder = Path.GetFullPath(workFolder);
            vanillaFolder = Path.GetFullPath(vanillaFolder);

            if (!Directory.Exists(workFolder))
            {
                result.Success = false;
                result.ErrorMessage = $"Work folder does not exist: {workFolder}";
                return result;
            }

            if (!Directory.Exists(vanillaFolder))
            {
                result.Success = false;
                result.ErrorMessage = $"Vanilla folder does not exist: {vanillaFolder}";
                return result;
            }

            // Get all files in work folder
            var workFiles = GetAllFiles(workFolder)
                .Select(f => GetRelativePath(workFolder, f))
                .Where(f => !ShouldIgnore(f))
                .ToList();

            // Get all files in vanilla folder
            var vanillaFilesSet = GetAllFiles(vanillaFolder)
                .Select(f => GetRelativePath(vanillaFolder, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var relativePath in workFiles)
            {
                string workFilePath = Path.Combine(workFolder, relativePath);
                string vanillaFilePath = Path.Combine(vanillaFolder, relativePath);

                if (!vanillaFilesSet.Contains(relativePath))
                {
                    // Brand new file - doesn't exist in vanilla
                    result.NewFiles[NormalizePath(relativePath)] = workFilePath;
                }
                else if (File.Exists(vanillaFilePath))
                {
                    // File exists in both - check if modified
                    bool isModified = !FilesAreEqual(workFilePath, vanillaFilePath);

                    if (isModified)
                    {
                        string fileName = Path.GetFileName(relativePath);
                        string ext = Path.GetExtension(relativePath).ToLowerInvariant();

                        // Check if this is a patchable JSON file
                        if (ext == ".json" && PatchableJsonFiles.Contains(fileName))
                        {
                            // Try to extract just the changed keys
                            var patch = ExtractJsonPatch(vanillaFilePath, workFilePath);
                            if (patch != null && patch.HasValues)
                            {
                                result.JsonPatches[NormalizePath(relativePath)] = patch;
                            }
                            else
                            {
                                // Couldn't patch, treat as full replacement
                                result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                            }
                        }
                        else if (fileName.Equals("plugins.js", StringComparison.OrdinalIgnoreCase))
                        {
                            // Special handling for plugins.js
                            var newPlugins = ExtractNewPlugins(vanillaFilePath, workFilePath);
                            result.NewPlugins.AddRange(newPlugins);

                            if (newPlugins.Count == 0)
                            {
                                // plugins.js changed but no new plugins detected - include as modified
                                result.Warnings.Add("plugins.js was modified but no new plugins were detected. Including as full replacement.");
                                result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                            }
                        }
                        else
                        {
                            // Regular modified file
                            result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Analysis failed: {ex.Message}";
        }

        return result;
    }

    // Generates a ModManifest from the analysis result
    public ModManifest GenerateManifest(PackerResult analysis, ModMetadata metadata)
    {
        var manifest = new ModManifest
        {
            Metadata = metadata
        };

        // Add new files as file_ops (source = target, files mirror game structure)
        foreach (var (relativePath, _) in analysis.NewFiles)
        {
            manifest.FileOps.Add(new FileOperation
            {
                Source = relativePath,
                Target = relativePath
            });
        }

        // Add modified files as file_ops
        foreach (var (relativePath, _) in analysis.ModifiedFiles)
        {
            manifest.FileOps.Add(new FileOperation
            {
                Source = relativePath,
                Target = relativePath
            });
        }

        // Add JSON patches
        foreach (var (targetPath, patchData) in analysis.JsonPatches)
        {
            manifest.JsonPatches.Add(new JsonPatch
            {
                Target = targetPath,
                MergeData = patchData
            });
        }

        // Add plugins
        manifest.PluginsRegistry.AddRange(analysis.NewPlugins);

        return manifest;
    }

    // Creates the complete mod package folder structure
    public void CreateModPackage(string outputFolder, ModManifest manifest, PackerResult analysis)
    {
        // Create output folder
        Directory.CreateDirectory(outputFolder);

        // Copy new files (directly into mod folder, mirroring game structure)
        foreach (var (relativePath, sourcePath) in analysis.NewFiles)
        {
            string destPath = Path.Combine(outputFolder, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            File.Copy(sourcePath, destPath, true);
        }

        // Copy modified files
        foreach (var (relativePath, sourcePath) in analysis.ModifiedFiles)
        {
            string destPath = Path.Combine(outputFolder, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            File.Copy(sourcePath, destPath, true);
        }

        // Write mod.json
        string manifestJson = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        File.WriteAllText(Path.Combine(outputFolder, "mod.json"), manifestJson);
    }

    #region Helper Methods

    private IEnumerable<string> GetAllFiles(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
    }

    private string GetRelativePath(string basePath, string fullPath)
    {
        return Path.GetRelativePath(basePath, fullPath);
    }

    private string NormalizePath(string path)
    {
        // Always use forward slashes for cross-platform compatibility
        return path.Replace('\\', '/');
    }

    private bool ShouldIgnore(string relativePath)
    {
        // Check if path starts with any ignored folder/file
        foreach (var ignored in IgnoredFiles)
        {
            if (relativePath.StartsWith(ignored, StringComparison.OrdinalIgnoreCase) ||
                relativePath.Contains($"/{ignored}/", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Contains($"\\{ignored}\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool FilesAreEqual(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);

        // Quick check: different sizes = definitely different
        if (info1.Length != info2.Length)
            return false;

        // For small files, compare byte-by-byte
        if (info1.Length < 1024 * 1024) // 1MB
        {
            return File.ReadAllBytes(path1).SequenceEqual(File.ReadAllBytes(path2));
        }

        // For larger files, use streaming comparison
        using var fs1 = File.OpenRead(path1);
        using var fs2 = File.OpenRead(path2);
        
        byte[] buffer1 = new byte[4096];
        byte[] buffer2 = new byte[4096];

        int read1, read2;
        while ((read1 = fs1.Read(buffer1, 0, buffer1.Length)) > 0 &&
               (read2 = fs2.Read(buffer2, 0, buffer2.Length)) > 0)
        {
            if (read1 != read2 || !buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                return false;
        }

        return true;
    }

    private JObject? ExtractJsonPatch(string vanillaPath, string workPath)
    {
        try
        {
            var vanillaJson = JObject.Parse(File.ReadAllText(vanillaPath));
            var workJson = JObject.Parse(File.ReadAllText(workPath));

            return GetJsonDifference(vanillaJson, workJson);
        }
        catch
        {
            return null; // JSON parsing failed, treat as binary
        }
    }

    // Recursively finds properties in 'modified' that differ from 'original'
    private JObject? GetJsonDifference(JObject original, JObject modified)
    {
        var diff = new JObject();

        foreach (var prop in modified.Properties())
        {
            var originalProp = original.Property(prop.Name);

            if (originalProp == null)
            {
                // New property - include it
                diff[prop.Name] = prop.Value.DeepClone();
            }
            else if (!JToken.DeepEquals(originalProp.Value, prop.Value))
            {
                // Property exists but changed
                if (originalProp.Value.Type == JTokenType.Object && prop.Value.Type == JTokenType.Object)
                {
                    // Recurse into nested objects
                    var nestedDiff = GetJsonDifference((JObject)originalProp.Value, (JObject)prop.Value);
                    if (nestedDiff != null && nestedDiff.HasValues)
                    {
                        diff[prop.Name] = nestedDiff;
                    }
                }
                else
                {
                    // Different value types or primitive change
                    diff[prop.Name] = prop.Value.DeepClone();
                }
            }
        }

        return diff.HasValues ? diff : null;
    }

    private List<PluginEntry> ExtractNewPlugins(string vanillaPath, string workPath)
    {
        var newPlugins = new List<PluginEntry>();

        try
        {
            var vanillaPlugins = ParsePluginsJs(File.ReadAllText(vanillaPath));
            var workPlugins = ParsePluginsJs(File.ReadAllText(workPath));

            // Find plugins in work that don't exist in vanilla
            var vanillaNames = vanillaPlugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in workPlugins)
            {
                if (!vanillaNames.Contains(plugin.Name))
                {
                    newPlugins.Add(plugin);
                }
            }
        }
        catch
        {
            // Failed to parse plugins.js
        }

        return newPlugins;
    }

    private List<PluginEntry> ParsePluginsJs(string content)
    {
        // plugins.js format: var $plugins = [ ... ];
        int startIndex = content.IndexOf('[');
        int endIndex = content.LastIndexOf(']');

        if (startIndex != -1 && endIndex != -1 && endIndex > startIndex)
        {
            string jsonPart = content.Substring(startIndex, endIndex - startIndex + 1);
            return JsonConvert.DeserializeObject<List<PluginEntry>>(jsonPart) ?? new();
        }

        return new();
    }

    #endregion
}
