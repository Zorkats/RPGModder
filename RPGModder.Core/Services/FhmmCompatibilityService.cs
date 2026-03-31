using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RPGModder.Core.Services;

// Provides compatibility with Mattie's Fear and Hunger Mod Manager (FHMM)
public class FhmmCompatibilityService
{
    // Check if FHMM is installed in a game directory
    public bool IsFhmmInstalled(string gameRoot)
    {
        var modsFolder = Path.Combine(gameRoot, "www", "mods");
        var indexHtml = Path.Combine(gameRoot, "www", "index.html");

        if (!Directory.Exists(modsFolder) || !File.Exists(indexHtml))
            return false;

        try
        {
            var content = File.ReadAllText(indexHtml);
            return content.Contains("modLoader") || content.Contains("MATTIE") || content.Contains("FearAndHungerModManager");
        }
        catch
        {
            return false;
        }
    }

    public string GetFhmmModsDirectory(string gameRoot)
    {
        return Path.Combine(gameRoot, "www", "mods");
    }

    public FhmmModConfig GenerateFhmmConfig(string modName, bool isDangerous = false, Dictionary<string, object>? parameters = null)
    {
        return new FhmmModConfig
        {
            Name = modName,
            Status = false,
            Parameters = parameters ?? new Dictionary<string, object>(),
            Danger = isDangerous,
            Dependencies = new List<string>()
        };
    }

    public void WriteFhmmConfig(string modFolder, FhmmModConfig config)
    {
        var configPath = Path.Combine(modFolder, $"{config.Name}.json");
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(configPath, json);
    }

    public List<FhmmMod> GetInstalledFhmmMods(string gameRoot)
    {
        var mods = new List<FhmmMod>();
        var modsFolder = GetFhmmModsDirectory(gameRoot);

        if (!Directory.Exists(modsFolder))
            return mods;

        foreach (var jsonFile in Directory.GetFiles(modsFolder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(jsonFile);
                var config = JsonConvert.DeserializeObject<FhmmModConfig>(json);
                if (config != null)
                {
                    var jsFile = Path.ChangeExtension(jsonFile, ".js");
                    mods.Add(new FhmmMod
                    {
                        Config = config,
                        JsonPath = jsonFile,
                        JsPath = File.Exists(jsFile) ? jsFile : null
                    });
                }
            }
            catch { }
        }

        return mods;
    }

    public bool IsPotentiallyDangerous(string modName, IEnumerable<string> modifiedFiles)
    {
        foreach (var file in modifiedFiles)
        {
            var lower = file.ToLowerInvariant();
            if (lower.Contains("save") || lower.Contains("database") || lower.Contains("system.json") ||
                lower.Contains("actors.json") || lower.Contains("items.json") || lower.Contains("weapons.json") ||
                lower.Contains("armors.json") || lower.Contains("classes.json") || lower.Contains("skills.json") ||
                lower.Contains("states.json") || lower.Contains("enemies.json") || lower.Contains("troops.json"))
            {
                return true;
            }
        }
        return false;
    }

    // --- NEW: RUNTIME SYNCHRONIZATION ---

    public bool IsFhmmMod(string modFolderPath)
    {
        string folderName = Path.GetFileName(modFolderPath);
        string fhmmJsonPath = Path.Combine(modFolderPath, $"{folderName}.json");
        string fhmmJsPath = Path.Combine(modFolderPath, $"{folderName}.js");
        return File.Exists(fhmmJsonPath) || File.Exists(fhmmJsPath);
    }

    // Synchronizes RPGModder's UI toggle state with FHMM's internal JSON configuration
    public void SyncFhmmModState(string activeProfilePath, ModListItem mod)
    {
        if (!IsFhmmMod(Path.Combine(activeProfilePath, mod.FolderName)))
            return;

        string fhmmJsonPath = Path.Combine(activeProfilePath, mod.FolderName, $"{mod.FolderName}.json");

        try
        {
            JObject fhmmConfig;

            if (File.Exists(fhmmJsonPath))
            {
                fhmmConfig = JObject.Parse(File.ReadAllText(fhmmJsonPath));
            }
            else
            {
                fhmmConfig = new JObject
                {
                    ["name"] = mod.FolderName,
                    ["parameters"] = new JObject(),
                    ["danger"] = true
                };
            }

            // Force FHMM's internal status to match RPGModder's UI toggle
            fhmmConfig["status"] = mod.IsEnabled;
            File.WriteAllText(fhmmJsonPath, fhmmConfig.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to sync FHMM state for {mod.FolderName}: {ex.Message}");
        }
    }

    // Enforces all toggles in the deployed www/mods directory before the game boots
    public void EnforceFhmmStates(string gameRoot, bool usesWwwFolder, IEnumerable<ModListItem> allProfileMods)
    {
        string deployedModsFolder = Path.Combine(gameRoot, usesWwwFolder ? "www/mods" : "mods");

        if (!Directory.Exists(deployedModsFolder))
            return;

        foreach (var mod in allProfileMods)
        {
            string deployedModPath = Path.Combine(deployedModsFolder, mod.FolderName);
            if (Directory.Exists(deployedModPath))
            {
                SyncFhmmModState(deployedModsFolder, mod);
            }
        }
    }
}

public class FhmmModConfig
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("status")]
    public bool Status { get; set; }

    [JsonProperty("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();

    [JsonProperty("danger")]
    public bool Danger { get; set; }

    [JsonProperty("dependencies")]
    public List<string> Dependencies { get; set; } = new();
}

public class FhmmMod
{
    public FhmmModConfig Config { get; set; } = new();
    public string JsonPath { get; set; } = "";
    public string? JsPath { get; set; }

    public bool IsEnabled => Config.Status;
    public bool IsDangerous => Config.Danger;
}