using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using System.Text;

namespace RPGModder.Core.Services;

public class ModEngine
{
    private readonly string _gamePath;        // Root folder (where Game.exe lives)
    private readonly string _contentPath;     // Where game content lives (either _gamePath or _gamePath/www)
    private readonly string _backupPath;
    private readonly string _modsRootPath;
    private readonly bool _usesWwwFolder;     // True for nw.js packaged games
    private readonly JsonMergeService _merger = new();

    private readonly string[] _protectedFolders = new[]
    {
        "data", "js", "img", "audio", "fonts", "css", "icon", "movies", "effects"
    };

    // --- Configuration ---
    public bool UseMerging { get; set; } = true;
    public bool UseSymlinks { get; set; } = false;
    public bool UseHardcoreMerging { get; set; } = false;

    // --- State & Reporting ---
    public bool JustCreatedSaveBackup { get; private set; } = false;
    public List<MergeReport> LastMergeReports { get; private set; } = new();

    public string ContentPath => _contentPath;
    public bool UsesWwwFolder => _usesWwwFolder;

    public ModEngine(string gameExecutablePath)
    {
        _gamePath = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty;

        // Detect if game uses www subfolder structure (common for nw.js packaged MV games)
        string wwwPath = Path.Combine(_gamePath, "www");
        _usesWwwFolder = Directory.Exists(wwwPath) &&
                         (Directory.Exists(Path.Combine(wwwPath, "js")) ||
                          Directory.Exists(Path.Combine(wwwPath, "data")));

        _contentPath = _usesWwwFolder ? wwwPath : _gamePath;

        // The "Vault" for clean vanilla files
        _backupPath = Path.Combine(_gamePath, "ModManager_Backups", "Clean_Vanilla");

        // The Mods directory
        _modsRootPath = Path.Combine(_gamePath, "Mods");

        if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);
        if (!Directory.Exists(_modsRootPath)) Directory.CreateDirectory(_modsRootPath);
    }

    // ==================================================================================
    // PHASE 1: SAFETY SYSTEMS (Time Capsule & Vanilla Backup)
    // ==================================================================================

    public void InitializeSafeState()
    {
        // 1. Content Backup (Assets & Data)
        string markerFile = Path.Combine(_backupPath, "backup_complete.marker");
        if (!File.Exists(markerFile))
        {
            foreach (var folder in _protectedFolders)
            {
                string sourcePath = Path.Combine(_contentPath, folder);
                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, Path.Combine(_backupPath, folder));
                }
            }

            File.WriteAllText(markerFile, JsonConvert.SerializeObject(new
            {
                BackupDate = DateTime.Now,
                UsesWwwFolder = _usesWwwFolder,
                ContentPath = _contentPath
            }, Formatting.Indented));
        }

        // 2. Save File "Time Capsule" (Protecting legacy saves)
        InitializeSafeSaves();
    }

    private void InitializeSafeSaves()
    {
        JustCreatedSaveBackup = false;

        // Locate real save folder
        string liveSavePath = Path.Combine(_contentPath, "save");
        if (!Directory.Exists(liveSavePath) && Directory.Exists(Path.Combine(_gamePath, "save")))
        {
            liveSavePath = Path.Combine(_gamePath, "save");
        }

        // Define the Time Capsule path
        string timeCapsulePath = Path.Combine(_gamePath, "ModManager_Backups", "Saves", "ORIGINAL_VANILLA");

        // ONLY run if the capsule doesn't exist yet (First run or first update to this version)
        if (!Directory.Exists(timeCapsulePath) && Directory.Exists(liveSavePath))
        {
            Directory.CreateDirectory(timeCapsulePath);

            var saves = Directory.GetFiles(liveSavePath, "*", SearchOption.TopDirectoryOnly)
                .Where(s => s.EndsWith(".rpgsave") || s.EndsWith(".rmmzsave"));

            bool anyBackedUp = false;
            foreach (var save in saves)
            {
                File.Copy(save, Path.Combine(timeCapsulePath, Path.GetFileName(save)));
                anyBackedUp = true;
            }

            if (anyBackedUp)
            {
                File.WriteAllText(Path.Combine(timeCapsulePath, "readme.txt"),
                    "These save files were backed up by RPGModder.\n" +
                    "They represent the state of your saves before you started using Profiles.\n" +
                    "Restore these if your saves ever disappear or get corrupted.");

                JustCreatedSaveBackup = true; // Signals UI to show notification
            }
        }
    }

    // ==================================================================================
    // PHASE 2: PROFILE & SAVE SWAPPING
    // ==================================================================================

    public void SwapSaveFiles(string oldProfileName, string newProfileName)
    {
        // 1. Identify Save Location
        string liveSavePath = Path.Combine(_contentPath, "save");
        if (!Directory.Exists(liveSavePath) && Directory.Exists(Path.Combine(_gamePath, "save")))
        {
            liveSavePath = Path.Combine(_gamePath, "save");
        }

        string storageRoot = Path.Combine(_gamePath, "ModManager_Backups", "ProfileSaves");
        string oldStorage = Path.Combine(storageRoot, oldProfileName);
        string newStorage = Path.Combine(storageRoot, newProfileName);

        if (!Directory.Exists(liveSavePath)) Directory.CreateDirectory(liveSavePath);
        if (!Directory.Exists(oldStorage)) Directory.CreateDirectory(oldStorage);
        if (!Directory.Exists(newStorage)) Directory.CreateDirectory(newStorage);

        // Identify valid save files
        var extensions = new[] { ".rpgsave", ".rmmzsave", "config.rpgsave", "global.rpgsave" };
        var liveFiles = Directory.GetFiles(liveSavePath)
            .Where(f => extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // SAFE SWAP PATTERN: use a temp staging area so a crash at any point is recoverable.
        //
        // Step 1: Copy live saves → old profile storage (backup current state)
        // Step 2: Copy new profile saves → temp staging directory
        // Step 3: Only after staging is fully written, swap live saves with staged saves
        //
        // If we crash during step 1-2, live saves are still intact.
        // If we crash during step 3, the staging dir has the complete set for recovery.

        string stagingDir = Path.Combine(storageRoot, $"_swap_staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            // Step 1: BACKUP live → old profile storage
            foreach (var file in liveFiles)
            {
                string fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(oldStorage, fileName), true);
            }

            // Step 2: STAGE new profile saves into temp directory
            if (Directory.Exists(newStorage))
            {
                foreach (var file in Directory.GetFiles(newStorage))
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(stagingDir, fileName), true);
                }
            }

            // Step 3: SWAP — delete live saves, move staged files in
            foreach (var file in liveFiles)
            {
                File.Delete(file);
            }

            foreach (var file in Directory.GetFiles(stagingDir))
            {
                string fileName = Path.GetFileName(file);
                string dest = Path.Combine(liveSavePath, fileName);
                File.Move(file, dest, true);
            }
        }
        finally
        {
            // Cleanup staging directory (safe even if swap failed — originals in oldStorage)
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
            catch { }
        }
    }

    // ==================================================================================
    // PHASE 3: REBUILDING ENGINE
    // ==================================================================================

    public void RebuildGame(ModProfile profile)
    {
        LastMergeReports.Clear();

        // 1. Restore Vanilla State
        foreach (var folder in _protectedFolders)
        {
            SmartRestoreFolder(folder);
        }

        // 2. Apply Mods
        if (UseMerging)
        {
            RebuildWithMerging(profile);
        }
        else
        {
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
        // Data structures for merging
        var fileOperations = new Dictionary<string, List<(string ModPath, FileOperation Op)>>(StringComparer.OrdinalIgnoreCase);
        var jsonPatches = new Dictionary<string, List<(string ModName, JObject Data)>>(StringComparer.OrdinalIgnoreCase);
        var allPlugins = new List<PluginEntry>();

        // 1. Collect all operations from all enabled mods
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

        // 2. Apply Merged File Operations
        foreach (var kvp in fileOperations)
        {
            string targetPath = kvp.Key;
            var operations = kvp.Value;
            string fullTargetPath = Path.Combine(_contentPath, targetPath);
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();

            // Special handling for JSON files with multiple sources -> Merge
            if (ext == ".json" && operations.Count > 1)
            {
                ApplyMergedJson(targetPath, operations);
            }
            else
            {
                // Non-JSON or single source -> Last one wins (Load Order)
                var lastOp = operations.Last();
                string sourceFile = ResolveModSourcePath(lastOp.ModPath, lastOp.Op.Source);
                if (File.Exists(sourceFile))
                {
                    CopyOrLinkFile(sourceFile, fullTargetPath);
                }
            }
        }

        // 3. Apply Merged JSON Patches
        foreach (var kvp in jsonPatches)
        {
            ApplyMergedJsonPatches(kvp.Key, kvp.Value);
        }

        // 4. Apply Merged Plugins
        if (allPlugins.Count > 0)
        {
            UpdatePluginsJs(allPlugins);
        }
    }

    // ==================================================================================
    // APPLICATION LOGIC (File Ops, Merging, Linking)
    // ==================================================================================

    private void ApplyMod(string modFolder, ModManifest manifest)
    {
        // 1. File Copy
        foreach (var op in manifest.FileOps)
        {
            string sourceFile = ResolveModSourcePath(modFolder, op.Source);
            string targetPath = NormalizeTargetPath(op.Target);
            string targetFile = Path.Combine(_contentPath, targetPath);

            if (File.Exists(sourceFile))
            {
                CopyOrLinkFile(sourceFile, targetFile);
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

    private void MergeJsonObjects(JToken original, JToken patch)
    {
        if (original.Type != JTokenType.Object || patch.Type != JTokenType.Object)
        {
            // If Hardcore is ON and both are arrays, try to merge
            if (UseHardcoreMerging && original.Type == JTokenType.Array && patch.Type == JTokenType.Array)
            {
                MergeArrays((JArray)original, (JArray)patch);
                return;
            }
            // Otherwise, simple replace
            return;
        }

        var originalObj = (JObject)original;
        var patchObj = (JObject)patch;

        foreach (var property in patchObj.Properties())
        {
            var originalProp = originalObj.Property(property.Name);

            if (originalProp == null)
            {
                originalObj.Add(property.Name, property.Value);
            }
            else
            {
                // Recursion
                if (originalProp.Value.Type == JTokenType.Object && property.Value.Type == JTokenType.Object)
                {
                    MergeJsonObjects(originalProp.Value, property.Value);
                }
                else if (UseHardcoreMerging && originalProp.Value.Type == JTokenType.Array && property.Value.Type == JTokenType.Array)
                {
                    MergeArrays((JArray)originalProp.Value, (JArray)property.Value);
                }
                else
                {
                    // Overwrite
                    originalProp.Value = property.Value;
                }
            }
        }
    }

    private void MergeArrays(JArray original, JArray patch)
    {
        // Hardcore Array Merging:
        // 1. If items have "id", match by ID.
        // 2. Otherwise, append unique items.

        foreach (var item in patch)
        {
            if (item.Type == JTokenType.Object && item["id"] != null)
            {
                // Try to find existing item with same ID
                var id = item["id"]?.ToString();
                var existing = original.FirstOrDefault(x => x["id"] != null && x["id"]?.ToString() == id);

                if (existing != null)
                {
                    // Recursive merge the object inside the array
                    MergeJsonObjects(existing, item);
                }
                else
                {
                    original.Add(item);
                }
            }
            else
            {
                // No ID, check if exact duplicate exists before appending
                if (!original.Any(x => JToken.DeepEquals(x, item)))
                {
                    original.Add(item);
                }
            }
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
                    // Remove existing entry with same name to avoid duplicates
                    currentPlugins.RemoveAll(p => p.Name == newPlugin.Name);
                    // Add new one
                    currentPlugins.Add(newPlugin);
                }

                string newJson = JsonConvert.SerializeObject(currentPlugins, Formatting.Indented);
                string newFileContent = $"// Generated by RPGModder\nvar $plugins =\n{newJson};\n";
                File.WriteAllText(fullPath, newFileContent);
            }
        }
    }

    // ==================================================================================
    // FILE SYSTEM HELPERS
    // ==================================================================================

    private void SmartRestoreFolder(string folderName)
    {
        string backupDir = Path.Combine(_backupPath, folderName);
        string gameDir = Path.Combine(_contentPath, folderName);

        if (!Directory.Exists(backupDir)) return;
        if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);

        // 1. Restore missing/altered files from Backup -> Game
        foreach (var backupFile in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(backupDir, backupFile);
            string gameFile = Path.Combine(gameDir, relativePath);

            if (!File.Exists(gameFile) || !FilesAreEqual(backupFile, gameFile))
            {
                // File is missing or modified -> Restore vanilla
                CopyOrLinkFile(backupFile, gameFile);
            }
        }

        // 2. Remove leftover mod files (files in Game but NOT in Backup)
        foreach (var gameFile in Directory.GetFiles(gameDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(gameDir, gameFile);
            string backupFile = Path.Combine(backupDir, relativePath);

            if (!File.Exists(backupFile))
            {
                File.Delete(gameFile);
            }
        }

        // Cleanup empty directories
        DeleteEmptyDirs(gameDir);
    }

    private void CopyOrLinkFile(string source, string dest)
    {
        string? destDir = Path.GetDirectoryName(dest);
        if (destDir != null) Directory.CreateDirectory(destDir);

        if (File.Exists(dest)) File.Delete(dest);

        if (UseSymlinks)
        {
            try
            {
                File.CreateSymbolicLink(dest, source);
                return;
            }
            catch
            {
                // Fallback silently to copy if link fails
            }
        }

        File.Copy(source, dest, true);
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);

        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

    private bool FilesAreEqual(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);

        // 1. Fast check: Size
        if (info1.Length != info2.Length) return false;

        try
        {
            // 2. Small files (<10MB): byte-level comparison
            if (info1.Length < 10 * 1024 * 1024)
            {
                byte[] file1 = File.ReadAllBytes(path1);
                byte[] file2 = File.ReadAllBytes(path2);
                return file1.SequenceEqual(file2);
            }

            // 3. Large files (>=10MB): SHA-256 hash comparison
            using var sha = System.Security.Cryptography.SHA256.Create();

            byte[] hash1, hash2;
            using (var stream1 = File.OpenRead(path1))
                hash1 = sha.ComputeHash(stream1);
            using (var stream2 = File.OpenRead(path2))
                hash2 = sha.ComputeHash(stream2);

            return hash1.SequenceEqual(hash2);
        }
        catch
        {
            // If we can't read (locked?), assume different to be safe
            return false;
        }
    }

    private void DeleteEmptyDirs(string startDir)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(startDir))
            {
                DeleteEmptyDirs(d);
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                {
                    Directory.Delete(d, false);
                }
            }
        }
        catch { }
    }

    // Resolves the source file path, checking multiple possible locations
    private string ResolveModSourcePath(string modFolder, string sourcePath)
    {
        // Try exact path first (works for most mods)
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
    // Strips www/ prefix from target paths for MV games (where _contentPath already points to www/).
    // Also handles any remaining nested prefixes as a safety net.
    private string NormalizeTargetPath(string targetPath)
    {
        string normalized = targetPath.Replace('\\', '/');

        // Strip everything up to and including "www/" (handles both "www/x" and "Folder/www/x")
        if (_usesWwwFolder)
        {
            int wwwIndex = normalized.LastIndexOf("www/", StringComparison.OrdinalIgnoreCase);
            if (wwwIndex >= 0)
                return normalized.Substring(wwwIndex + 4);
        }

        // For non-www games or paths without www/, strip any prefix before known game folders
        string[] knownRoots = { "data/", "js/", "img/", "audio/", "fonts/", "css/", "icon/", "movies/", "effects/" };
        foreach (var root in knownRoots)
        {
            int idx = normalized.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return normalized.Substring(idx);
        }

        return targetPath;
    }
}