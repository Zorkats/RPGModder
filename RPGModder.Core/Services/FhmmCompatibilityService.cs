using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace RPGModder.Core.Services;

// Provides compatibility with Mattie's Fear and Hunger Mod Manager (FHMM)
// https://github.com/mattieFM/FearAndHungerModManager
public class FhmmCompatibilityService
{
    // Check if FHMM is installed in a game directory
    public bool IsFhmmInstalled(string gameRoot)
    {
        // FHMM installs to www/mods/ and modifies index.html
        var modsFolder = Path.Combine(gameRoot, "www", "mods");
        var indexHtml = Path.Combine(gameRoot, "www", "index.html");
        
        if (!Directory.Exists(modsFolder))
            return false;
            
        // Check if index.html contains FHMM loader reference
        if (File.Exists(indexHtml))
        {
            var content = File.ReadAllText(indexHtml);
            if (content.Contains("modLoader") || content.Contains("MATTIE") || content.Contains("FearAndHungerModManager"))
                return true;
        }
        
        // Check for FHMM-specific files
        var commonLibs = Path.Combine(modsFolder, "commonLibs");
        return Directory.Exists(commonLibs);
    }
    
    // Get the FHMM mods directory for a game
    public string GetFhmmModsDirectory(string gameRoot)
    {
        return Path.Combine(gameRoot, "www", "mods");
    }
    
    // Generate FHMM-compatible mod config JSON
    public FhmmModConfig GenerateFhmmConfig(string modName, bool isDangerous = false, Dictionary<string, object>? parameters = null)
    {
        return new FhmmModConfig
        {
            Name = modName,
            Status = false, // Disabled by default until user enables
            Parameters = parameters ?? new Dictionary<string, object>(),
            Danger = isDangerous,
            Dependencies = new List<string>()
        };
    }
    
    // Write FHMM config file for a mod
    public void WriteFhmmConfig(string modFolder, FhmmModConfig config)
    {
        var configPath = Path.Combine(modFolder, $"{config.Name}.json");
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(configPath, json);
    }
    
    // Read existing FHMM mods from a game directory
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
            catch
            {
                // Skip invalid JSON files
            }
        }
        
        return mods;
    }
    
    // Check if a mod name suggests it might affect save files
    public bool IsPotentiallyDangerous(string modName, IEnumerable<string> modifiedFiles)
    {
        // Mods that modify certain files are considered dangerous for saves
        foreach (var file in modifiedFiles)
        {
            var lower = file.ToLowerInvariant();
            if (lower.Contains("save") || 
                lower.Contains("database") || 
                lower.Contains("system.json") ||
                lower.Contains("actors.json") ||
                lower.Contains("items.json") ||
                lower.Contains("weapons.json") ||
                lower.Contains("armors.json") ||
                lower.Contains("classes.json") ||
                lower.Contains("skills.json") ||
                lower.Contains("states.json") ||
                lower.Contains("enemies.json") ||
                lower.Contains("troops.json"))
            {
                return true;
            }
        }
        
        return false;
    }
}

// FHMM mod configuration format
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

// Represents an installed FHMM mod
public class FhmmMod
{
    public FhmmModConfig Config { get; set; } = new();
    public string JsonPath { get; set; } = "";
    public string? JsPath { get; set; }
    
    public bool IsEnabled => Config.Status;
    public bool IsDangerous => Config.Danger;
}
