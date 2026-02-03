using Newtonsoft.Json;

namespace RPGModder.Core.Services;

// Persists app settings and cached game list
public class SettingsService
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RPGModder"
    );
    
    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");
    private static readonly string GamesCache = Path.Combine(SettingsFolder, "games.json");

    public AppSettings Settings { get; private set; } = new();
    public List<DetectedGame> CachedGames { get; private set; } = new();

    public SettingsService()
    {
        EnsureFolder();
        Load();
    }

    private void EnsureFolder()
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
                Directory.CreateDirectory(SettingsFolder);
        }
        catch { }
    }

    public void Load()
    {
        // Load settings
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { Settings = new AppSettings(); }

        // Load cached games
        try
        {
            if (File.Exists(GamesCache))
            {
                var json = File.ReadAllText(GamesCache);
                CachedGames = JsonConvert.DeserializeObject<List<DetectedGame>>(json) ?? new List<DetectedGame>();
                
                // Validate cached games still exist
                CachedGames = CachedGames.Where(g => File.Exists(g.ExePath)).ToList();
            }
        }
        catch { CachedGames = new List<DetectedGame>(); }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }
        catch { }
    }

    public void SaveGames(IEnumerable<DetectedGame> games)
    {
        try
        {
            CachedGames = games.ToList();
            File.WriteAllText(GamesCache, JsonConvert.SerializeObject(CachedGames, Formatting.Indented));
        }
        catch { }
    }

    public void AddGame(DetectedGame game)
    {
        if (!CachedGames.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            CachedGames.Add(game);
            SaveGames(CachedGames);
        }
    }

    public static string GetSettingsFolder() => SettingsFolder;
}

public class AppSettings
{
    public string? LastGamePath { get; set; }
    public bool AutoScanOnStartup { get; set; } = false;
    public DateTime LastScanTime { get; set; }
    
    // Nexus Mods settings
    public string? NexusApiKey { get; set; }
    public bool NxmProtocolRegistered { get; set; } = false;
    
    // Game to Nexus domain mappings (game exe path -> nexus domain)
    public Dictionary<string, NexusGameMapping> NexusGameMappings { get; set; } = new();
    
    // Mod management settings
    public bool SmartMergeEnabled { get; set; } = true;
    public bool ConfirmBeforeRemove { get; set; } = true;
    public bool AutoApplyChanges { get; set; } = false;
}

public class NexusGameMapping
{
    public string NexusDomain { get; set; } = "";
    public string NexusGameName { get; set; } = "";
    public int NexusGameId { get; set; }
}
