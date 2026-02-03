using Newtonsoft.Json.Linq;

namespace RPGModder.Core.Models;

// Result of comparing a modded folder against a vanilla folder
public class PackerResult
{
    // Files that exist in work but not in vanilla (brand new)
    public Dictionary<string, string> NewFiles { get; set; } = new();

    // Files that exist in both but have different content
    public Dictionary<string, string> ModifiedFiles { get; set; } = new();

    // JSON files with specific key changes for smart patching
    public Dictionary<string, JObject> JsonPatches { get; set; } = new();

    // New plugin entries detected in plugins.js
    public List<PluginEntry> NewPlugins { get; set; } = new();

    // Warnings generated during analysis
    public List<string> Warnings { get; set; } = new();

    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public int TotalChanges => NewFiles.Count + ModifiedFiles.Count + JsonPatches.Count + NewPlugins.Count;
}
