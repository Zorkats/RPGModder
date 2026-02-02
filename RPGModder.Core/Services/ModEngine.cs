using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

public class ModEngine
{
    private readonly string _gamePath;
    private readonly string _backupPath;
    private readonly string _modsRootPath; // Where we store the mod zips/folders

    public ModEngine(string gameExecutablePath)
    {
        _gamePath = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty;

        // This is the "Vault" where we keep the clean files
        _backupPath = Path.Combine(_gamePath, "ModManager_Backups", "Clean_Vanilla");

        // This is where users should drag their mods into
        _modsRootPath = Path.Combine(_gamePath, "Mods");

        if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);
        if (!Directory.Exists(_modsRootPath)) Directory.CreateDirectory(_modsRootPath);
    }

    // --- PHASE 1: SAFETY SYSTEM ---

    public void InitializeSafeState()
    {
        // If we haven't created a clean backup yet, do it NOW.
        // We assume the first time the user runs this tool, their game is clean.
        // (In v1.1 we can add a 'Verify Integrity' check)

        string markerFile = Path.Combine(_backupPath, "backup_complete.marker");
        if (File.Exists(markerFile)) return; // Already backed up

        // Backup 'data' folder
        CopyDirectory(Path.Combine(_gamePath, "data"), Path.Combine(_backupPath, "data"));

        // Backup 'js' folder
        CopyDirectory(Path.Combine(_gamePath, "js"), Path.Combine(_backupPath, "js"));

        // Backup 'www' if it exists (Deployment mode)
        if (Directory.Exists(Path.Combine(_gamePath, "www")))
             CopyDirectory(Path.Combine(_gamePath, "www"), Path.Combine(_backupPath, "www"));

        File.WriteAllText(markerFile, "Backup created on " + DateTime.Now);
    }

    public void RebuildGame(ModProfile profile)
    {
        // 1. RESTORE VANILLA (The "Undo" Button)
        // We delete the current game data to remove any old mod traces
        RestoreFolder("data");
        RestoreFolder("js");
        if (Directory.Exists(Path.Combine(_backupPath, "www"))) RestoreFolder("www");

        // 2. APPLY MODS IN ORDER
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
                        // We reuse the logic from Phase 1, but now it runs on a fresh copy
                        ApplyMod(modPath, manifest);
                    }
                }
                catch
                {
                    // If a mod fails, we log it but continue (or stop depending on preference)
                    // For now, silent continue so one bad mod doesn't stop the rest
                }
            }
        }
    }

    // --- HELPER: FILE SYSTEM ---

    private void RestoreFolder(string folderName)
    {
        string targetPath = Path.Combine(_gamePath, folderName);
        string sourcePath = Path.Combine(_backupPath, folderName);

        // Safety: Only delete if we actually HAVE a backup to restore
        if (!Directory.Exists(sourcePath)) return;

        // Nuke the dirty folder
        if (Directory.Exists(targetPath))
            Directory.Delete(targetPath, true);

        // Copy the clean folder back
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

    // --- PHASE 2: APPLICATION LOGIC (Recycled from previous step) ---

    private void ApplyMod(string modFolder, ModManifest manifest)
    {
        // This is the EXACT same logic as before, just private now
        // 1. File Copy
        foreach (var op in manifest.FileOps)
        {
            string sourceFile = Path.Combine(modFolder, op.Source);
            string targetFile = Path.Combine(_gamePath, op.Target);

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
            ApplyJsonPatch(patch.Target, patch.MergeData);
        }

        // 3. Plugins
        if (manifest.PluginsRegistry.Count > 0)
        {
            UpdatePluginsJs(manifest.PluginsRegistry);
        }
    }

    private void ApplyJsonPatch(string targetFileRelative, JObject mergeData)
    {
        string fullPath = Path.Combine(_gamePath, targetFileRelative);
        // [Insert your robust path checking for 'www' here]

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
        // [Insert your robust plugins.js logic here]
        // Same as before: Read file -> Parse -> Add -> Write
        // Note: We DO NOT need to backup plugins.js here anymore
        // because 'RebuildGame' already restored a clean copy!

        string pluginsPath = Path.Combine("js", "plugins.js");
        string fullPath = Path.Combine(_gamePath, pluginsPath);

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