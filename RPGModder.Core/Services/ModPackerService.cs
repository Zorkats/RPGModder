using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

// The Auto-Packer service - compares modded vs vanilla folders and generates mod.json
public class ModPackerService
{
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

            // Short-Circuit 1: Filter ignored files immediately
            var workFiles = GetAllFiles(workFolder)
                .Select(f => GetRelativePath(workFolder, f))
                .Where(f => FilePolicyService.GetPackerAction(f) != FileAction.Ignore)
                .ToList();

            var vanillaFilesSet = GetAllFiles(vanillaFolder)
                .Select(f => GetRelativePath(vanillaFolder, f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var relativePath in workFiles)
            {
                string workFilePath = Path.Combine(workFolder, relativePath);
                string vanillaFilePath = Path.Combine(vanillaFolder, relativePath);

                FileAction action = FilePolicyService.GetPackerAction(relativePath);

                if (!vanillaFilesSet.Contains(relativePath))
                {
                    // Brand new file - doesn't exist in vanilla
                    result.NewFiles[NormalizePath(relativePath)] = workFilePath;
                }
                else if (File.Exists(vanillaFilePath))
                {
                    bool isModified = false;

                    // Short-Circuit 2: Bypass expensive byte-comparisons for semantic files
                    if (action == FileAction.ForceInclude)
                    {
                        isModified = true;
                        string warning = $"Detected semantic configuration file ({relativePath}). Forced inclusion applied.";
                        if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
                    }
                    else
                    {
                        // File exists in both - check if modified
                        isModified = !FilesAreEqual(workFilePath, vanillaFilePath);
                    }

                    if (isModified)
                    {
                        string fileName = Path.GetFileName(relativePath);
                        string ext = Path.GetExtension(relativePath).ToLowerInvariant();

                        // Check if this is a patchable JSON file
                        if (ext == ".json" && PatchableJsonFiles.Contains(fileName))
                        {
                            var patch = ExtractJsonPatch(vanillaFilePath, workFilePath);
                            if (patch != null && patch.HasValues)
                            {
                                result.JsonPatches[NormalizePath(relativePath)] = patch;
                            }
                            else
                            {
                                result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                            }
                        }
                        else if (fileName.Equals("plugins.js", StringComparison.OrdinalIgnoreCase))
                        {
                            var newPlugins = ExtractNewPlugins(vanillaFilePath, workFilePath);
                            result.NewPlugins.AddRange(newPlugins);

                            if (newPlugins.Count == 0)
                            {
                                result.Warnings.Add("plugins.js was modified but no new plugins were detected. Including as full replacement.");
                                result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                            }
                        }
                        else
                        {
                            result.ModifiedFiles[NormalizePath(relativePath)] = workFilePath;
                        }
                    }
                }
            }

            foreach (var (relativePath, sourcePath) in result.NewFiles)
            {
                if (IsPluginFile(relativePath))
                {
                    string pluginName = Path.GetFileNameWithoutExtension(relativePath);

                    if (!result.NewPlugins.Any(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var entry = GeneratePluginEntryFromFile(sourcePath, pluginName);
                        result.NewPlugins.Add(entry);
                        result.Warnings.Add($"Auto-detected plugin script '{pluginName}'. Default parameters applied.");
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

    public ModManifest GenerateManifest(PackerResult analysis, ModMetadata metadata)
    {
        var manifest = new ModManifest
        {
            Metadata = metadata
        };

        foreach (var (relativePath, _) in analysis.NewFiles)
            manifest.FileOps.Add(new FileOperation { Source = relativePath, Target = relativePath });

        foreach (var (relativePath, _) in analysis.ModifiedFiles)
            manifest.FileOps.Add(new FileOperation { Source = relativePath, Target = relativePath });

        foreach (var (targetPath, patchData) in analysis.JsonPatches)
            manifest.JsonPatches.Add(new JsonPatch { Target = targetPath, MergeData = patchData });

        manifest.PluginsRegistry.AddRange(analysis.NewPlugins);

        // HYDRATION: Calculate FHMM status exactly once during packing
        manifest.Metadata.IsFhmmMod = manifest.FileOps.Any(op =>
            op.Target.Contains("mods/", StringComparison.OrdinalIgnoreCase) ||
            op.Target.Contains("_moddata/", StringComparison.OrdinalIgnoreCase));

        return manifest;
    }

    public void CreateModPackage(string outputFolder, ModManifest manifest, PackerResult analysis)
    {
        Directory.CreateDirectory(outputFolder);

        foreach (var (relativePath, sourcePath) in analysis.NewFiles)
        {
            string destPath = Path.Combine(outputFolder, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            File.Copy(sourcePath, destPath, true);
        }

        foreach (var (relativePath, sourcePath) in analysis.ModifiedFiles)
        {
            string destPath = Path.Combine(outputFolder, relativePath);
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            File.Copy(sourcePath, destPath, true);
        }

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
        return path.Replace('\\', '/');
    }

    private bool FilesAreEqual(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);

        if (info1.Length != info2.Length)
            return false;

        if (info1.Length < 1024 * 1024)
        {
            return File.ReadAllBytes(path1).SequenceEqual(File.ReadAllBytes(path2));
        }

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

    // Fields in System.json that should NEVER be captured by the packer (usually user-specific settings)
    private static readonly HashSet<string> ExcludedSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenWidth", "screenHeight", "uiAreaWidth", "uiAreaHeight", 
        "windowLineWidth", "locale", "title1Name", "title2Name"
    };

    private JObject? ExtractJsonPatch(string vanillaPath, string workPath)
    {
        try
        {
            var vanillaJson = JObject.Parse(File.ReadAllText(vanillaPath));
            var workJson = JObject.Parse(File.ReadAllText(workPath));
            string fileName = Path.GetFileName(vanillaPath);

            return GetJsonDifference(vanillaJson, workJson, fileName);
        }
        catch
        {
            return null;
        }
    }

    private JObject? GetJsonDifference(JObject original, JObject modified, string? fileName = null)
    {
        var diff = new JObject();

        foreach (var prop in modified.Properties())
        {
            // NEW: Skip excluded fields for specific files (like System.json resolution)
            if (fileName != null && fileName.Equals("system.json", StringComparison.OrdinalIgnoreCase))
            {
                if (ExcludedSystemFields.Contains(prop.Name)) continue;
            }

            var originalProp = original.Property(prop.Name);

            if (originalProp == null)
            {
                diff[prop.Name] = prop.Value.DeepClone();
            }
            else if (!JToken.DeepEquals(originalProp.Value, prop.Value))
            {
                if (originalProp.Value.Type == JTokenType.Object && prop.Value.Type == JTokenType.Object)
                {
                    var nestedDiff = GetJsonDifference((JObject)originalProp.Value, (JObject)prop.Value, fileName);
                    if (nestedDiff != null && nestedDiff.HasValues)
                    {
                        diff[prop.Name] = nestedDiff;
                    }
                }
                else
                {
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

            var vanillaNames = vanillaPlugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in workPlugins)
            {
                if (!vanillaNames.Contains(plugin.Name))
                {
                    newPlugins.Add(plugin);
                }
            }
        }
        catch { }

        return newPlugins;
    }

    private List<PluginEntry> ParsePluginsJs(string content)
    {
        int startIndex = content.IndexOf('[');
        int endIndex = content.LastIndexOf(']');

        if (startIndex != -1 && endIndex != -1 && endIndex > startIndex)
        {
            string jsonPart = content.Substring(startIndex, endIndex - startIndex + 1);
            return JsonConvert.DeserializeObject<List<PluginEntry>>(jsonPart) ?? new();
        }

        return new();
    }

    private bool IsPluginFile(string relativePath)
    {
        string normalized = NormalizePath(relativePath);
        return (normalized.IndexOf("js/plugins/", StringComparison.OrdinalIgnoreCase) >= 0)
               && normalized.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }

    private PluginEntry GeneratePluginEntryFromFile(string filePath, string name)
    {
        string description = "";

        try
        {
            foreach (var line in File.ReadLines(filePath).Take(50))
            {
                if (line.Contains("@plugindesc"))
                {
                    var match = Regex.Match(line, @"@plugindesc\s+(.*)");
                    if (match.Success)
                    {
                        description = match.Groups[1].Value.Trim();
                        break;
                    }
                }
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(description))
        {
            description = "(Auto-detected) No description found in script file.";
        }

        return new PluginEntry
        {
            Name = name,
            Status = true,
            Description = description,
            Parameters = new Dictionary<string, string>()
        };
    }

    #endregion
}