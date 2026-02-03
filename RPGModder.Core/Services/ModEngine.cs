using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

public class ModEngine
{
    private readonly string _gamePath;        // Root folder (where Game.exe lives)
    private readonly string _contentPath;     // Where game content lives (either _gamePath or _gamePath/www)
    private readonly string _backupPath;
    private readonly string _modsRootPath;
    private readonly bool _usesWwwFolder;     // True for nw.js packaged games
    private readonly JsonMergeService _merger = new();

    // Enable/disable smart merging (can be toggled in settings)
    public bool UseMerging { get; set; } = true;
    public List<MergeReport> LastMergeReports { get; private set; } = new();

    public ModEngine(string gameExecutablePath)
    {
        _gamePath = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty;

        // Detect if game uses www subfolder structure (common for nw.js packaged MV games)
        string wwwPath = Path.Combine(_gamePath, "www");
        _usesWwwFolder = Directory.Exists(wwwPath) && 
                         (Directory.Exists(Path.Combine(wwwPath, "js")) || 
                          Directory.Exists(Path.Combine(wwwPath, "data")));

        _contentPath = _usesWwwFolder ? wwwPath : _gamePath;

        // This is the "Vault" where we keep the clean files
        _backupPath = Path.Combine(_gamePath, "ModManager_Backups", "Clean_Vanilla");

        // This is where users should drag their mods into
        _modsRootPath = Path.Combine(_gamePath, "Mods");

        if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);
        if (!Directory.Exists(_modsRootPath)) Directory.CreateDirectory(_modsRootPath);
    }

    public string ContentPath => _contentPath;
    public bool UsesWwwFolder => _usesWwwFolder;

    // --- PHASE 1: SAFETY SYSTEM ---

    public void InitializeSafeState()
    {
        string markerFile = Path.Combine(_backupPath, "backup_complete.marker");
        if (File.Exists(markerFile)) return; // Already backed up

        // Backup content folders from the correct location
        string dataPath = Path.Combine(_contentPath, "data");
        string jsPath = Path.Combine(_contentPath, "js");

        if (Directory.Exists(dataPath))
            CopyDirectory(dataPath, Path.Combine(_backupPath, "data"));

        if (Directory.Exists(jsPath))
            CopyDirectory(jsPath, Path.Combine(_backupPath, "js"));

        // Save structure info
        File.WriteAllText(markerFile, JsonConvert.SerializeObject(new
        {
            BackupDate = DateTime.Now,
            UsesWwwFolder = _usesWwwFolder,
            ContentPath = _contentPath
        }));
    }

    public void RebuildGame(ModProfile profile)
    {
        LastMergeReports.Clear();

        // 1. RESTORE VANILLA
        RestoreFolder("data");
        RestoreFolder("js");

        if (UseMerging)
        {
            // Smart merge approach: collect all mod changes per file, then merge
            RebuildWithMerging(profile);
        }
        else
        {
            // Legacy approach: apply mods sequentially (last wins)
            RebuildSequential(profile);
        }
    }

    private void RebuildSequential(ModProfile profile)
    {
        foreach (string modFolderName in profile.EnabledMods)
        {
            string modPath = Path.Combine(_modsRootPath, modFolderName);
            string manifestPath = Path.Combine(modPath, "mod.json");

            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    var manifest = JsonConvert.DeserializeObject<ModManifest>(json);

                    if (manifest != null)
                    {
                        ApplyMod(modPath, manifest);
                    }
                }
                catch { }
            }
        }
    }

    private void RebuildWithMerging(ModProfile profile)
    {
        // Collect all file operations grouped by target file
        var fileOperations = new Dictionary<string, List<(string ModPath, FileOperation Op)>>(StringComparer.OrdinalIgnoreCase);
        var jsonPatches = new Dictionary<string, List<(string ModName, JObject Data)>>(StringComparer.OrdinalIgnoreCase);
        var allPlugins = new List<PluginEntry>();

        foreach (string modFolderName in profile.EnabledMods)
        {
            string modPath = Path.Combine(_modsRootPath, modFolderName);
            string manifestPath = Path.Combine(modPath, "mod.json");

            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(manifestPath));
                if (manifest == null) continue;

                // Collect file operations
                foreach (var op in manifest.FileOps)
                {
                    string targetPath = NormalizeTargetPath(op.Target);
                    if (!fileOperations.ContainsKey(targetPath))
                        fileOperations[targetPath] = new List<(string, FileOperation)>();
                    fileOperations[targetPath].Add((modPath, op));
                }

                // Collect JSON patches
                foreach (var patch in manifest.JsonPatches)
                {
                    string targetPath = NormalizeTargetPath(patch.Target);
                    if (!jsonPatches.ContainsKey(targetPath))
                        jsonPatches[targetPath] = new List<(string, JObject)>();
                    jsonPatches[targetPath].Add((modFolderName, patch.MergeData));
                }

                // Collect plugins
                allPlugins.AddRange(manifest.PluginsRegistry);
            }
            catch { }
        }

        // Apply merged file operations
        foreach (var kvp in fileOperations)
        {
            string targetPath = kvp.Key;
            var operations = kvp.Value;
            string fullTargetPath = Path.Combine(_contentPath, targetPath);
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();

            // For JSON files with multiple sources, try to merge
            if (ext == ".json" && operations.Count > 1)
            {
                ApplyMergedJson(targetPath, operations);
            }
            else
            {
                // Non-JSON or single source: last wins
                var lastOp = operations.Last();
                string sourceFile = ResolveModSourcePath(lastOp.ModPath, lastOp.Op.Source);
                if (File.Exists(sourceFile))
                {
                    string? targetDir = Path.GetDirectoryName(fullTargetPath);
                    if (targetDir != null) Directory.CreateDirectory(targetDir);
                    File.Copy(sourceFile, fullTargetPath, true);
                }
            }
        }

        // Apply merged JSON patches
        foreach (var kvp in jsonPatches)
        {
            ApplyMergedJsonPatches(kvp.Key, kvp.Value);
        }

        // Apply plugins (merge the list)
        if (allPlugins.Count > 0)
        {
            UpdatePluginsJs(allPlugins);
        }
    }

    private void ApplyMergedJson(string targetPath, List<(string ModPath, FileOperation Op)> operations)
    {
        string fullTargetPath = Path.Combine(_contentPath, targetPath);
        string backupPath = Path.Combine(_backupPath, targetPath);

        // Get base (vanilla) content
        string baseJson = "[]";
        if (File.Exists(backupPath))
            baseJson = File.ReadAllText(backupPath);
        else if (File.Exists(fullTargetPath))
            baseJson = File.ReadAllText(fullTargetPath);

        // Collect all mod versions
        var modJsons = new List<string>();
        foreach (var (modPath, op) in operations)
        {
            string sourceFile = ResolveModSourcePath(modPath, op.Source);
            if (File.Exists(sourceFile))
            {
                modJsons.Add(File.ReadAllText(sourceFile));
            }
        }

        if (modJsons.Count == 0) return;

        // Merge!
        string mergedJson = _merger.MergeJsonFiles(baseJson, modJsons, Path.GetFileName(targetPath));
        LastMergeReports.Add(_merger.LastReport);

        // Write merged result
        string? targetDir = Path.GetDirectoryName(fullTargetPath);
        if (targetDir != null) Directory.CreateDirectory(targetDir);
        File.WriteAllText(fullTargetPath, mergedJson);
    }

    private void ApplyMergedJsonPatches(string targetPath, List<(string ModName, JObject Data)> patches)
    {
        string fullPath = Path.Combine(_contentPath, targetPath);
        if (!File.Exists(fullPath)) return;

        try
        {
            string jsonContent = File.ReadAllText(fullPath);
            JObject original = JObject.Parse(jsonContent);

            foreach (var (modName, patchData) in patches)
            {
                MergeJsonObjects(original, patchData);
            }

            File.WriteAllText(fullPath, original.ToString(Formatting.Indented));
        }
        catch { }
    }

    // --- HELPER: FILE SYSTEM ---

    private void RestoreFolder(string folderName)
    {
        // Target is in content path (could be www/ or root)
        string targetPath = Path.Combine(_contentPath, folderName);
        string sourcePath = Path.Combine(_backupPath, folderName);

        if (!Directory.Exists(sourcePath)) return;

        if (Directory.Exists(targetPath))
            Directory.Delete(targetPath, true);

        CopyDirectory(sourcePath, targetPath);
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);

        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

    // --- PHASE 2: APPLICATION LOGIC ---

    private void ApplyMod(string modFolder, ModManifest manifest)
    {
        // 1. File Copy - files go to content path
        foreach (var op in manifest.FileOps)
        {
            string sourceFile = ResolveModSourcePath(modFolder, op.Source);
            string targetPath = NormalizeTargetPath(op.Target);
            string targetFile = Path.Combine(_contentPath, targetPath);

            if (File.Exists(sourceFile))
            {
                string? targetDir = Path.GetDirectoryName(targetFile);
                if (targetDir != null) Directory.CreateDirectory(targetDir);
                File.Copy(sourceFile, targetFile, true);
            }
        }

        // 2. JSON Patch
        foreach (var patch in manifest.JsonPatches)
        {
            string targetPath = NormalizeTargetPath(patch.Target);
            ApplyJsonPatch(targetPath, patch.MergeData);
        }

        // 3. Plugins
        if (manifest.PluginsRegistry.Count > 0)
        {
            UpdatePluginsJs(manifest.PluginsRegistry);
        }
    }

    // Resolves the source file path, checking multiple possible locations
    private string ResolveModSourcePath(string modFolder, string sourcePath)
    {
        // Try exact path first
        string exactPath = Path.Combine(modFolder, sourcePath);
        if (File.Exists(exactPath)) return exactPath;

        // If game uses www/ and source doesn't have it, try with www/ prefix
        if (_usesWwwFolder && !sourcePath.StartsWith("www", StringComparison.OrdinalIgnoreCase))
        {
            string wwwPath = Path.Combine(modFolder, "www", sourcePath);
            if (File.Exists(wwwPath)) return wwwPath;
        }

        // If source has www/ prefix, try without it
        if (sourcePath.StartsWith("www/", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.StartsWith("www\\", StringComparison.OrdinalIgnoreCase))
        {
            string withoutWww = Path.Combine(modFolder, sourcePath.Substring(4));
            if (File.Exists(withoutWww)) return withoutWww;
        }

        return exactPath; // Return original path (will fail gracefully)
    }

    // Normalizes target path - strips www/ prefix if game uses www folder
    private string NormalizeTargetPath(string targetPath)
    {
        // If target has www/ prefix and game uses www folder, strip it
        // (because _contentPath already points to www/)
        if (_usesWwwFolder &&
            (targetPath.StartsWith("www/", StringComparison.OrdinalIgnoreCase) ||
             targetPath.StartsWith("www\\", StringComparison.OrdinalIgnoreCase)))
        {
            return targetPath.Substring(4);
        }

        return targetPath;
    }

    private void ApplyJsonPatch(string targetFileRelative, JObject mergeData)
    {
        string fullPath = Path.Combine(_contentPath, targetFileRelative);

        if (File.Exists(fullPath))
        {
            string jsonContent = File.ReadAllText(fullPath);
            JObject original = JObject.Parse(jsonContent);
            MergeJsonObjects(original, mergeData);
            File.WriteAllText(fullPath, original.ToString(Formatting.Indented));
        }
    }

    private void MergeJsonObjects(JObject original, JObject patch)
    {
        foreach (var property in patch.Properties())
        {
            var originalProp = original.Property(property.Name);
            if (originalProp == null)
                original.Add(property.Name, property.Value);
            else if (originalProp.Value.Type == JTokenType.Object && property.Value.Type == JTokenType.Object)
                MergeJsonObjects((JObject)originalProp.Value, (JObject)property.Value);
            else
                originalProp.Value = property.Value;
        }
    }

    private void UpdatePluginsJs(List<PluginEntry> newPlugins)
    {
        string fullPath = Path.Combine(_contentPath, "js", "plugins.js");

        if (File.Exists(fullPath))
        {
            string rawContent = File.ReadAllText(fullPath);
            int startIndex = rawContent.IndexOf('[');
            int endIndex = rawContent.LastIndexOf(']');

            if (startIndex != -1 && endIndex != -1)
            {
                string jsonPart = rawContent.Substring(startIndex, endIndex - startIndex + 1);
                var currentPlugins = JsonConvert.DeserializeObject<List<PluginEntry>>(jsonPart) ?? new();

                foreach (var newPlugin in newPlugins)
                {
                    currentPlugins.RemoveAll(p => p.Name == newPlugin.Name);
                    currentPlugins.Add(newPlugin);
                }

                string newJson = JsonConvert.SerializeObject(currentPlugins, Formatting.Indented);
                string newFileContent = $"// Generated by RPGModder\nvar $plugins =\n{newJson};\n";
                File.WriteAllText(fullPath, newFileContent);
            }
        }
    }
}