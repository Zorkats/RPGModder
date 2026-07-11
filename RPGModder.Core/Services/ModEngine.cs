using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RPGModder.Core.Services;

public class ModEngine
{
    private readonly string _gamePath;
    private readonly string _contentPath;
    private readonly string _backupPath;
    private readonly string _modsRootPath;
    private readonly bool _usesWwwFolder;
    private readonly JsonMergeService _merger = new();
    private readonly GameWorkspacePaths _workspace;

    // --- Configuration ---
    public bool UseMerging { get; set; } = true;
    public bool UseSymlinks { get; set; } = false;
    public bool UseHardcoreMerging { get; set; } = false;

    // --- State & Reporting ---
    public bool JustCreatedSaveBackup { get; private set; } = false;
    public List<MergeReport> LastMergeReports { get; private set; } = new();
    public OperationResult LastOperationResult { get; private set; } = new();
    public IReadOnlyList<string> RecoveredTransactions { get; }
    public WorkspaceMigrationResult MigrationResult { get; }

    public string ContentPath => _contentPath;
    public bool UsesWwwFolder => _usesWwwFolder;

    public ModEngine(string gameExecutablePath)
    {
        _gamePath = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty;

        string wwwPath = Path.Combine(_gamePath, "www");
        _usesWwwFolder = Directory.Exists(wwwPath) &&
                         (Directory.Exists(Path.Combine(wwwPath, "js")) ||
                          Directory.Exists(Path.Combine(wwwPath, "data")));

        _contentPath = _usesWwwFolder ? wwwPath : _gamePath;
        _workspace = new GameWorkspacePaths(_gamePath);
        MigrationResult = new WorkspaceMigrationService().ImportLegacyLayout(_workspace);
        _backupPath = _workspace.CleanBackup;
        _modsRootPath = _workspace.Mods;

        if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);
        if (!Directory.Exists(_modsRootPath)) Directory.CreateDirectory(_modsRootPath);
        RecoveredTransactions = DeploymentTransaction.RecoverInterrupted(
            _contentPath,
            _workspace.Transactions,
            FilePolicyService.TransactionalFolders,
            FilePolicyService.ProtectedRootFiles);
    }

    // ==================================================================================
    // PHASE 1: SAFETY SYSTEMS (Time Capsule & Vanilla Backup)
    // ==================================================================================

    public void InitializeSafeState()
    {
        string markerFile = Path.Combine(_backupPath, "backup_complete.marker");

        if (!File.Exists(markerFile))
        {
            // --- NEW: DIRTY INSTALLATION TRIPWIRE ---
            if (IsDirtyInstallation())
            {
                throw new InvalidOperationException(
                    "Dirty installation detected! It looks like Fear & Hunger Mod Manager (FHMM) or another runtime loader " +
                    "is already installed directly into your base game.\n\n" +
                    "To use RPGModder safely, you must have a 100% clean, unmodified game. " +
                    "Please verify your game files on Steam/Itch.io to restore the vanilla state, then install FHMM as a standard ZIP mod inside RPGModder."
                );
            }

            foreach (var folder in FilePolicyService.ProtectedFolders)
            {
                string sourcePath = Path.Combine(_contentPath, folder);
                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, Path.Combine(_backupPath, folder));
                }
            }

            // Backup standalone root files
            foreach (var file in FilePolicyService.ProtectedRootFiles)
            {
                string sourceFile = Path.Combine(_contentPath, file);
                if (File.Exists(sourceFile))
                {
                    File.Copy(sourceFile, Path.Combine(_backupPath, file), true);
                }
            }

            File.WriteAllText(markerFile, JsonConvert.SerializeObject(new
            {
                BackupDate = DateTime.Now,
                UsesWwwFolder = _usesWwwFolder,
                ContentPath = _contentPath
            }, Formatting.Indented));
        }

        InitializeSafeSaves();
    }

    private bool IsDirtyInstallation()
    {
        // We no longer check for the mere existence of the "mods" folder because
        // Steam's "Verify Integrity" leaves orphaned folders behind.
        // A game is only truly "dirty" if the boot sequence is actively hijacked.

        string indexHtml = Path.Combine(_contentPath, "index.html");
        if (File.Exists(indexHtml))
        {
            string content = File.ReadAllText(indexHtml);

            // Matches Mattie's HTML comments, script tags, and TY Mod Loader
            if (content.Contains("mattieFMModLoader.js", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("MATTIE", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("TY_ModLoader", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private void InitializeSafeSaves()
    {
        JustCreatedSaveBackup = false;

        string liveSavePath = Path.Combine(_contentPath, "save");
        if (!Directory.Exists(liveSavePath) && Directory.Exists(Path.Combine(_gamePath, "save")))
        {
            liveSavePath = Path.Combine(_gamePath, "save");
        }

        string timeCapsulePath = Path.Combine(_workspace.SaveBackups, "original-vanilla");

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

                JustCreatedSaveBackup = true;
            }
        }
    }

    // ==================================================================================
    // PHASE 2: PROFILE & SAVE SWAPPING
    // ==================================================================================

    public void SwapSaveFiles(string oldProfileName, string newProfileName)
    {
        string liveSavePath = Path.Combine(_contentPath, "save");
        if (!Directory.Exists(liveSavePath) && Directory.Exists(Path.Combine(_gamePath, "save")))
        {
            liveSavePath = Path.Combine(_gamePath, "save");
        }

        string storageRoot = _workspace.ProfileSaves;
        string oldStorage = SafePathService.ResolveContainedPath(
            storageRoot,
            SafePathService.ValidateDirectoryName(oldProfileName, "Old profile name"),
            "Old profile saves");
        string newStorage = SafePathService.ResolveContainedPath(
            storageRoot,
            SafePathService.ValidateDirectoryName(newProfileName, "New profile name"),
            "New profile saves");

        if (!Directory.Exists(liveSavePath)) Directory.CreateDirectory(liveSavePath);
        if (!Directory.Exists(oldStorage)) Directory.CreateDirectory(oldStorage);
        if (!Directory.Exists(newStorage)) Directory.CreateDirectory(newStorage);

        var extensions = new[] { ".rpgsave", ".rmmzsave", "config.rpgsave", "global.rpgsave" };
        var liveFiles = Directory.GetFiles(liveSavePath)
            .Where(f => extensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        string stagingDir = Path.Combine(storageRoot, $"_swap_staging_{Guid.NewGuid():N}");
        string rollbackDir = Path.Combine(storageRoot, $"_swap_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(rollbackDir);

        try
        {
            foreach (var file in liveFiles)
            {
                string fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(oldStorage, fileName), true);
                File.Copy(file, Path.Combine(rollbackDir, fileName), true);
            }

            if (Directory.Exists(newStorage))
            {
                foreach (var file in Directory.GetFiles(newStorage))
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(stagingDir, fileName), true);
                }
            }

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
        catch
        {
            foreach (string file in Directory.GetFiles(liveSavePath))
            {
                if (extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(file);
                }
            }

            foreach (string file in Directory.GetFiles(rollbackDir))
            {
                File.Copy(file, Path.Combine(liveSavePath, Path.GetFileName(file)), true);
            }

            throw;
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
            catch { }
            try { if (Directory.Exists(rollbackDir)) Directory.Delete(rollbackDir, true); }
            catch { }
        }
    }

    // ==================================================================================
    // PHASE 3: REBUILDING ENGINE
    // ==================================================================================

    public OperationResult RebuildGame(ModProfile profile)
    {
        LastMergeReports.Clear();
        LastOperationResult = new OperationResult();

        using var transaction = DeploymentTransaction.Begin(
            _contentPath,
            _workspace.Transactions,
            FilePolicyService.TransactionalFolders,
            FilePolicyService.ProtectedRootFiles);

        try
        {
            RebuildGameCore(profile, LastOperationResult);
            if (!LastOperationResult.Success)
            {
                transaction.Rollback();
                WriteDeploymentHistory(profile, LastOperationResult);
                return LastOperationResult;
            }

            transaction.Commit();
            WriteDeploymentHistory(profile, LastOperationResult);
            return LastOperationResult;
        }
        catch (Exception ex)
        {
            LastOperationResult.AddError("deployment.failed", ex.Message);
            transaction.Rollback();
            WriteDeploymentHistory(profile, LastOperationResult);
            return LastOperationResult;
        }
    }

    private void RebuildGameCore(ModProfile profile, OperationResult result)
    {

        // 1. Restore Vanilla State
        foreach (var folder in FilePolicyService.ProtectedFolders)
        {
            SmartRestoreFolder(folder);
        }

        // Restore standalone root files
        foreach (var file in FilePolicyService.ProtectedRootFiles)
        {
            string backupFile = Path.Combine(_backupPath, file);
            string gameFile = Path.Combine(_contentPath, file);

            if (File.Exists(backupFile))
            {
                if (!File.Exists(gameFile) || !FilesAreEqual(backupFile, gameFile))
                {
                    CopyOrLinkFile(backupFile, gameFile);
                }
            }
        }

        // 2. Apply Mods
        if (UseMerging)
        {
            RebuildWithMerging(profile, result);
        }
        else
        {
            RebuildSequential(profile, result);
        }

        // 3. Synchronize FHMM JSON states right before the game is allowed to launch
        var fhmmService = new FhmmCompatibilityService();
        var profileMods = profile.EnabledMods.Select(name => new ModListItem(new ModManifest(), name, true));
        fhmmService.EnforceFhmmStates(_gamePath, _usesWwwFolder, profileMods);
    }

    private void RebuildSequential(ModProfile profile, OperationResult result)
    {
        foreach (string modFolderName in profile.EnabledMods)
        {
            string modPath = SafePathService.ResolveContainedPath(
                _modsRootPath,
                SafePathService.ValidateDirectoryName(modFolderName, "Enabled mod name"),
                "Enabled mod path");
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
                catch (Exception ex)
                {
                    result.AddError("manifest.invalid", ex.Message, modFolderName);
                }
            }
            else
            {
                result.AddError("manifest.missing", "The enabled mod has no mod.json file.", modFolderName);
            }
        }
    }

    private void RebuildWithMerging(ModProfile profile, OperationResult result)
    {
        var fileOperations = new Dictionary<string, List<(string ModPath, FileOperation Op)>>(StringComparer.OrdinalIgnoreCase);
        var jsonPatches = new Dictionary<string, List<(string ModName, JObject Data)>>(StringComparer.OrdinalIgnoreCase);
        var allPlugins = new List<PluginEntry>();

        foreach (string modFolderName in profile.EnabledMods)
        {
            string modPath = SafePathService.ResolveContainedPath(
                _modsRootPath,
                SafePathService.ValidateDirectoryName(modFolderName, "Enabled mod name"),
                "Enabled mod path");
            string manifestPath = Path.Combine(modPath, "mod.json");

            if (!File.Exists(manifestPath))
            {
                result.AddError("manifest.missing", "The enabled mod has no mod.json file.", modFolderName);
                continue;
            }

            try
            {
                var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(manifestPath));
                if (manifest == null) continue;

                foreach (var op in manifest.FileOps)
                {
                    string targetPath = NormalizeTargetPath(op.Target);
                    if (!fileOperations.ContainsKey(targetPath))
                        fileOperations[targetPath] = new List<(string, FileOperation)>();
                    fileOperations[targetPath].Add((modPath, op));
                }

                foreach (var patch in manifest.JsonPatches)
                {
                    string targetPath = NormalizeTargetPath(patch.Target);
                    if (!jsonPatches.ContainsKey(targetPath))
                        jsonPatches[targetPath] = new List<(string, JObject)>();
                    jsonPatches[targetPath].Add((modFolderName, patch.MergeData));
                }

                allPlugins.AddRange(manifest.PluginsRegistry);
            }
            catch (Exception ex)
            {
                result.AddError("manifest.invalid", ex.Message, modFolderName);
            }
        }

        if (!result.Success)
        {
            return;
        }

        foreach (var kvp in fileOperations)
        {
            string targetPath = kvp.Key;
            var operations = kvp.Value;
            string fullTargetPath = SafePathService.ResolveContainedPath(_contentPath, targetPath, "Manifest target");
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();
            string fileName = Path.GetFileName(targetPath).ToLowerInvariant();

            // Intercept semantic configuration files via Policy Engine
            if (FilePolicyService.IsConfigSave(fileName))
            {
                if (UseMerging)
                {
                    ApplyMergedLzConfig(targetPath, operations);
                    continue;
                }
            }

            if (ext == ".json" && operations.Count > 1)
            {
                ApplyMergedJson(targetPath, operations);
            }
            else
            {
                var lastOp = operations.Last();
                string sourceFile = ResolveModSourcePath(lastOp.ModPath, lastOp.Op.Source);
                if (!File.Exists(sourceFile))
                {
                    throw new FileNotFoundException("A manifest source file is missing.", sourceFile);
                }

                CopyOrLinkFile(sourceFile, fullTargetPath);
            }
        }

        foreach (var kvp in jsonPatches)
        {
            ApplyMergedJsonPatches(kvp.Key, kvp.Value);
        }

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
        foreach (var op in manifest.FileOps)
        {
            string sourceFile = ResolveModSourcePath(modFolder, op.Source);
            string targetPath = NormalizeTargetPath(op.Target);
            string targetFile = SafePathService.ResolveContainedPath(_contentPath, targetPath, "Manifest target");

            if (!File.Exists(sourceFile))
            {
                throw new FileNotFoundException("A manifest source file is missing.", sourceFile);
            }

            CopyOrLinkFile(sourceFile, targetFile);
        }

        foreach (var patch in manifest.JsonPatches)
        {
            string targetPath = NormalizeTargetPath(patch.Target);
            ApplyJsonPatch(targetPath, patch.MergeData);
        }

        if (manifest.PluginsRegistry.Count > 0)
        {
            UpdatePluginsJs(manifest.PluginsRegistry);
        }
    }

    // Hand-offs LZ-String Base64 config files to the JsonMergeService directly into isolated vault
    private void ApplyMergedLzConfig(string targetPath, List<(string ModPath, FileOperation Op)> operations)
    {
        string baseConfigPath = Path.Combine(_gamePath, _usesWwwFolder ? "www/save" : "save", Path.GetFileName(targetPath));
        string baseContent = File.Exists(baseConfigPath) ? File.ReadAllText(baseConfigPath) : "{}";

        var modContents = new List<string>();
        foreach (var (modPath, op) in operations)
        {
            string sourceFile = ResolveModSourcePath(modPath, op.Source);
            if (!File.Exists(sourceFile))
            {
                throw new FileNotFoundException("A manifest source file is missing.", sourceFile);
            }

            modContents.Add(File.ReadAllText(sourceFile));
        }

        if (modContents.Count == 0) return;

        string mergedLzString = _merger.MergeLzStringConfigFiles(baseContent, modContents, Path.GetFileName(targetPath));

        Directory.CreateDirectory(Path.GetDirectoryName(baseConfigPath)!);
        File.WriteAllText(baseConfigPath, mergedLzString);
        LastMergeReports.Add(_merger.LastReport);
    }

    private void ApplyMergedJson(string targetPath, List<(string ModPath, FileOperation Op)> operations)
    {
        string fullTargetPath = SafePathService.ResolveContainedPath(_contentPath, targetPath, "Manifest target");
        string backupPath = SafePathService.ResolveContainedPath(_backupPath, targetPath, "Backup target");

        string baseJson = "[]";
        if (File.Exists(backupPath))
            baseJson = File.ReadAllText(backupPath);
        else if (File.Exists(fullTargetPath))
            baseJson = File.ReadAllText(fullTargetPath);

        var modJsons = new List<string>();
        foreach (var (modPath, op) in operations)
        {
            string sourceFile = ResolveModSourcePath(modPath, op.Source);
            if (!File.Exists(sourceFile))
            {
                throw new FileNotFoundException("A manifest source file is missing.", sourceFile);
            }

            modJsons.Add(File.ReadAllText(sourceFile));
        }

        if (modJsons.Count == 0) return;

        string mergedJson = _merger.MergeJsonFiles(baseJson, modJsons, Path.GetFileName(targetPath));
        LastMergeReports.Add(_merger.LastReport);

        string? targetDir = Path.GetDirectoryName(fullTargetPath);
        if (targetDir != null) Directory.CreateDirectory(targetDir);
        File.WriteAllText(fullTargetPath, mergedJson);
    }

    private void ApplyMergedJsonPatches(string targetPath, List<(string ModName, JObject Data)> patches)
    {
        string fullPath = SafePathService.ResolveContainedPath(_contentPath, targetPath, "JSON patch target");
        if (!File.Exists(fullPath)) return;

        string jsonContent = File.ReadAllText(fullPath);
        JObject original = JObject.Parse(jsonContent);

        foreach (var (_, patchData) in patches)
        {
            MergeJsonObjects(original, patchData);
        }

        File.WriteAllText(fullPath, original.ToString(Formatting.Indented));
    }

    private void ApplyJsonPatch(string targetFileRelative, JObject mergeData)
    {
        string fullPath = SafePathService.ResolveContainedPath(_contentPath, targetFileRelative, "JSON patch target");

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
            if (UseHardcoreMerging && original.Type == JTokenType.Array && patch.Type == JTokenType.Array)
            {
                MergeArrays((JArray)original, (JArray)patch);
                return;
            }
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
                    originalProp.Value = property.Value;
                }
            }
        }
    }

    private void MergeArrays(JArray original, JArray patch)
    {
        foreach (var item in patch)
        {
            if (item.Type == JTokenType.Object && item["id"] != null)
            {
                var id = item["id"]?.ToString();
                var existing = original.FirstOrDefault(x => x["id"] != null && x["id"]?.ToString() == id);

                if (existing != null)
                {
                    MergeJsonObjects(existing, item);
                }
                else
                {
                    original.Add(item);
                }
            }
            else
            {
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
                    currentPlugins.RemoveAll(p => p.Name == newPlugin.Name);
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

        foreach (var backupFile in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(backupDir, backupFile);
            string gameFile = Path.Combine(gameDir, relativePath);

            if (!File.Exists(gameFile) || !FilesAreEqual(backupFile, gameFile))
            {
                CopyOrLinkFile(backupFile, gameFile);
            }
        }

        foreach (var gameFile in Directory.GetFiles(gameDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(gameDir, gameFile);
            string backupFile = Path.Combine(backupDir, relativePath);

            if (!File.Exists(backupFile))
            {
                File.Delete(gameFile);
            }
        }

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
            catch { }
        }

        File.Copy(source, dest, true);
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        FileTreeService.CopyDirectory(sourceDir, destinationDir);
    }

    private bool FilesAreEqual(string path1, string path2)
    {
        var info1 = new FileInfo(path1);
        var info2 = new FileInfo(path2);

        if (info1.Length != info2.Length) return false;

        try
        {
            if (info1.Length < 10 * 1024 * 1024)
            {
                byte[] file1 = File.ReadAllBytes(path1);
                byte[] file2 = File.ReadAllBytes(path2);
                return file1.SequenceEqual(file2);
            }

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

    private string ResolveModSourcePath(string modFolder, string sourcePath)
    {
        string exactPath = SafePathService.ResolveContainedPath(modFolder, sourcePath, "Manifest source");
        if (File.Exists(exactPath)) return exactPath;

        if (_usesWwwFolder && !sourcePath.StartsWith("www", StringComparison.OrdinalIgnoreCase))
        {
            string wwwPath = SafePathService.ResolveContainedPath(modFolder, Path.Combine("www", sourcePath), "Manifest source");
            if (File.Exists(wwwPath)) return wwwPath;
        }

        if (sourcePath.StartsWith("www/", StringComparison.OrdinalIgnoreCase) ||
            sourcePath.StartsWith("www\\", StringComparison.OrdinalIgnoreCase))
        {
            string withoutWww = SafePathService.ResolveContainedPath(modFolder, sourcePath.Substring(4), "Manifest source");
            if (File.Exists(withoutWww)) return withoutWww;
        }

        return exactPath;
    }

    private string NormalizeTargetPath(string targetPath)
    {
        string normalized = targetPath.Replace('\\', '/');

        if (_usesWwwFolder)
        {
            int wwwIndex = normalized.LastIndexOf("www/", StringComparison.OrdinalIgnoreCase);
            if (wwwIndex >= 0)
                return normalized.Substring(wwwIndex + 4);
        }

        string[] knownRoots = { "data/", "js/", "img/", "audio/", "fonts/", "css/", "icon/", "movies/", "effects/" };
        foreach (var root in knownRoots)
        {
            int idx = normalized.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return normalized.Substring(idx);
        }

        return targetPath;
    }

    private void WriteDeploymentHistory(ModProfile profile, OperationResult result)
    {
        var entry = new
        {
            timestampUtc = DateTime.UtcNow,
            success = result.Success,
            enabledMods = profile.EnabledMods.ToArray(),
            diagnostics = result.Diagnostics
        };
        string path = Path.Combine(_workspace.History, $"deployment-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
        FileTreeService.WriteAllTextAtomic(path, JsonConvert.SerializeObject(entry, Formatting.Indented));
    }
}
