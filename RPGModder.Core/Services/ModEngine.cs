using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

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
    private readonly VanillaStore _vanilla;
    private readonly SaveStore _saves;

    // --- Configuration ---
    public bool UseMerging { get; set; } = true;
    public bool UseHardcoreMerging { get; set; } = false;

    // --- State & Reporting ---
    public bool JustCreatedSaveBackup { get; private set; } = false;
    public List<MergeReport> LastMergeReports { get; private set; } = new();
    public OperationResult LastOperationResult { get; private set; } = new();
    public IReadOnlyList<string> RecoveredTransactions { get; }
    public WorkspaceMigrationResult MigrationResult { get; }

    public string ContentPath => _contentPath;
    public bool UsesWwwFolder => _usesWwwFolder;

    /// <summary>
    /// Opens a game without a session, for tests of the legacy engine. The session passes its own
    /// workspace paths and layout instead (OWN-1); A7 deletes this class.
    /// </summary>
    public ModEngine(string gameExecutablePath)
        : this(
            gameExecutablePath,
            new GameWorkspacePaths(Path.GetDirectoryName(gameExecutablePath) ?? string.Empty),
            GameLayout.Detect(Path.GetDirectoryName(gameExecutablePath) ?? string.Empty),
            new FileSystemOps())
    {
    }

    public ModEngine(string gameExecutablePath, GameWorkspacePaths workspace, GameLayout layout, IFileSystemOps files)
    {
        _gamePath = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty;
        _usesWwwFolder = layout.UsesWww;
        _contentPath = layout.ContentRoot;
        _workspace = workspace;
        _vanilla = new VanillaStore(workspace, layout, files);
        _saves = new SaveStore(workspace, layout, files);
        MigrationResult = new WorkspaceMigrationService(files).ImportLegacyLayout(_workspace);
        _backupPath = _vanilla.Root;
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

    public const string BackupMarkerFileName = VanillaStore.MarkerFileName;

    public string BackupMarkerPath => _vanilla.MarkerPath;

    public void InitializeSafeState()
    {
        _vanilla.EnsureBackup();
        JustCreatedSaveBackup = _saves.EnsureTimeCapsule();
    }

    // ==================================================================================
    // PHASE 2: PROFILE & SAVE SWAPPING
    // ==================================================================================

    public void SwapSaveFiles(string oldProfileName, string newProfileName)
    {
        _saves.Swap(oldProfileName, newProfileName);
    }

    // ==================================================================================
    // PHASE 3: REBUILDING ENGINE
    // ==================================================================================

    public OperationResult RebuildGame(ModProfile profile)
    {
        LastMergeReports.Clear();
        LastOperationResult = new OperationResult();

        // Two mods that add the same new file with different bytes have no base to merge against.
        // The deploy stops before anything is written, on both the merging and the sequential path.
        try
        {
            AddUnresolvedNewFileCollisions(profile, LastOperationResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A mod name or target the deploy would refuse anyway, or a source that cannot be read.
            LastOperationResult.AddError(DiagnosticCodes.DeploymentFailed, ex.Message);
        }

        if (!LastOperationResult.Success)
        {
            TryWriteDeploymentHistory(profile, LastOperationResult);
            return LastOperationResult;
        }

        using var transaction = DeploymentTransaction.Begin(
            _contentPath,
            _workspace.Transactions,
            FilePolicyService.TransactionalFolders,
            FilePolicyService.ProtectedRootFiles);

        try
        {
            RebuildGameCore(profile, LastOperationResult);
            if (LastOperationResult.Success)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }
        }
        catch (Exception ex)
        {
            LastOperationResult.AddError(DiagnosticCodes.DeploymentFailed, ex.Message);
            transaction.Rollback();
        }

        // History is a record of the outcome, never part of it: a failed write must not
        // roll back a committed deployment.
        TryWriteDeploymentHistory(profile, LastOperationResult);
        return LastOperationResult;
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
                    CopyFile(backupFile, gameFile);
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
                    ModManifest manifest = ModStore.ReadManifestFile(manifestPath);
                    if (!manifest.IsSupported)
                    {
                        AddUnsupportedManifest(result, manifest, modFolderName);
                        continue;
                    }

                    ApplyMod(modPath, manifest);
                }
                catch (Exception ex)
                {
                    result.AddError(DiagnosticCodes.ManifestInvalid, ex.Message, modFolderName);
                }
            }
            else
            {
                result.AddError(DiagnosticCodes.ManifestMissing, "The enabled mod has no mod.json file.", modFolderName);
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
                result.AddError(DiagnosticCodes.ManifestMissing, "The enabled mod has no mod.json file.", modFolderName);
                continue;
            }

            try
            {
                ModManifest manifest = ModStore.ReadManifestFile(manifestPath);
                if (!manifest.IsSupported)
                {
                    AddUnsupportedManifest(result, manifest, modFolderName);
                    continue;
                }

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
                result.AddError(DiagnosticCodes.ManifestInvalid, ex.Message, modFolderName);
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

                CopyFile(sourceFile, fullTargetPath);
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

            CopyFile(sourceFile, targetFile);
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
                CopyFile(backupFile, gameFile);
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

    private static void CopyFile(string source, string dest)
    {
        string? destDir = Path.GetDirectoryName(dest);
        if (destDir != null) Directory.CreateDirectory(destDir);

        // Delete first so that a link left by an older version is replaced by a real file instead of
        // having its target overwritten.
        if (File.Exists(dest)) File.Delete(dest);

        File.Copy(source, dest, true);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unequal makes the caller copy the backup file over the game file, which is the safe side.
            Trace.TraceWarning($"Files could not be compared and are treated as different: {path1}: {ex.Message}");
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind changes no file the game loads.
            Trace.TraceWarning($"Empty folders under {startDir} could not be removed: {ex.Message}");
        }
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

    private void AddUnresolvedNewFileCollisions(ModProfile profile, OperationResult result)
    {
        var suppliers = new Dictionary<string, List<(string ModFolderName, string SourceFile)>>(StringComparer.OrdinalIgnoreCase);
        foreach (string modFolderName in profile.EnabledMods)
        {
            ModManifest? manifest = TryReadManifestForGuard(modFolderName, out string modPath);
            if (manifest == null)
            {
                continue;
            }

            foreach (FileOperation op in manifest.FileOps)
            {
                string targetPath = NormalizeTargetPath(op.Target);
                if (FilePolicyService.IsConfigSave(Path.GetFileName(targetPath).ToLowerInvariant()))
                {
                    continue;
                }

                if (!suppliers.TryGetValue(targetPath, out var list))
                {
                    list = new List<(string, string)>();
                    suppliers[targetPath] = list;
                }

                list.Add((modFolderName, ResolveModSourcePath(modPath, op.Source)));
            }
        }

        foreach (var (targetPath, list) in suppliers)
        {
            if (list.Count < 2 || HasVanillaCounterpart(targetPath))
            {
                continue;
            }

            int distinctContents = list
                .Where(item => File.Exists(item.SourceFile))
                .Select(item => HashFile(item.SourceFile))
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (distinctContents < 2)
            {
                continue;
            }

            string mods = string.Join(", ", list.Select(item => item.ModFolderName).Distinct(StringComparer.OrdinalIgnoreCase));
            result.AddError(
                DiagnosticCodes.DeployUnresolvedCollisions,
                $"Mods {mods} add different files at a path the game does not have.",
                targetPath);
        }
    }

    private ModManifest? TryReadManifestForGuard(string modFolderName, out string modPath)
    {
        modPath = SafePathService.ResolveContainedPath(
            _modsRootPath,
            SafePathService.ValidateDirectoryName(modFolderName, "Enabled mod name"),
            "Enabled mod path");
        string manifestPath = Path.Combine(modPath, "mod.json");
        if (!File.Exists(manifestPath))
        {
            // The deploy reports the missing manifest itself, with manifest.missing.
            return null;
        }

        try
        {
            ModManifest manifest = ModStore.ReadManifestFile(manifestPath);
            // The deploy reports a manifest newer than this build itself, with manifest.schema_unsupported.
            return manifest.IsSupported ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // The deploy reports the unreadable manifest itself, with manifest.invalid.
            Trace.TraceWarning($"The collision guard skips an unreadable manifest: {modFolderName}: {ex.Message}");
            return null;
        }
    }

    private static void AddUnsupportedManifest(OperationResult result, ModManifest manifest, string modFolderName)
    {
        result.AddError(
            DiagnosticCodes.ManifestSchemaUnsupported,
            $"The mod declares manifest schema version {manifest.EffectiveSchemaVersion}, which is newer than this version supports.",
            modFolderName);
    }

    private bool HasVanillaCounterpart(string targetPath)
    {
        return _vanilla.Contains(targetPath);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void TryWriteDeploymentHistory(ModProfile profile, OperationResult result)
    {
        try
        {
            WriteDeploymentHistory(profile, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.AddWarning(DiagnosticCodes.HistoryWriteFailed, $"The deployment history could not be recorded: {ex.Message}");
        }
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
