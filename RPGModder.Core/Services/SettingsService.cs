using Newtonsoft.Json;

namespace RPGModder.Core.Services;

// Persists app settings and cached game list
public class SettingsService
{
    public const int CurrentSchemaVersion = 2;
    private static readonly string DefaultSettingsFolder = ResolveDefaultSettingsFolder();
    private readonly string _settingsFolder;
    private readonly string _settingsFile;
    private readonly string _gamesCache;

    public AppSettings Settings { get; private set; } = new();
    public List<DetectedGame> CachedGames { get; private set; } = new();
    public string? LastPersistenceError { get; private set; }

    public SettingsService(string? settingsFolder = null)
    {
        _settingsFolder = settingsFolder ?? DefaultSettingsFolder;
        _settingsFile = Path.Combine(_settingsFolder, "settings.json");
        _gamesCache = Path.Combine(_settingsFolder, "games.json");
        EnsureFolder();
        Load();
    }

    private void EnsureFolder()
    {
        try
        {
            if (!Directory.Exists(_settingsFolder))
                Directory.CreateDirectory(_settingsFolder);
            ProtectPath(_settingsFolder, isDirectory: true);
        }
        catch { }
    }

    public void Load()
    {
        // Load settings
        try
        {
            if (File.Exists(_settingsFile))
            {
                var json = File.ReadAllText(_settingsFile);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                MigrateSettings();
            }
        }
        catch (Exception ex)
        {
            LastPersistenceError = ex.Message;
            Settings = new AppSettings();
        }

        // Load cached games
        try
        {
            if (File.Exists(_gamesCache))
            {
                var json = File.ReadAllText(_gamesCache);
                CachedGames = JsonConvert.DeserializeObject<List<DetectedGame>>(json) ?? new List<DetectedGame>();
                
                // Validate cached games still exist
                CachedGames = CachedGames.Where(g => File.Exists(g.ExePath)).ToList();
            }
        }
        catch (Exception ex)
        {
            LastPersistenceError = ex.Message;
            CachedGames = new List<DetectedGame>();
        }
    }

    public void Save()
    {
        try
        {
            Settings.SchemaVersion = CurrentSchemaVersion;
            FileTreeService.WriteAllTextAtomic(_settingsFile, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            ProtectPath(_settingsFile, isDirectory: false);
            LastPersistenceError = null;
        }
        catch (Exception ex)
        {
            LastPersistenceError = ex.Message;
        }
    }

    public void SaveGames(IEnumerable<DetectedGame> games)
    {
        try
        {
            CachedGames = games.ToList();
            FileTreeService.WriteAllTextAtomic(_gamesCache, JsonConvert.SerializeObject(CachedGames, Formatting.Indented));
            ProtectPath(_gamesCache, isDirectory: false);
            LastPersistenceError = null;
        }
        catch (Exception ex)
        {
            LastPersistenceError = ex.Message;
        }
    }

    public void AddGame(DetectedGame game)
    {
        if (!CachedGames.Any(g => g.ExePath.Equals(game.ExePath, PlatformService.PathComparison)))
        {
            CachedGames.Add(game);
            SaveGames(CachedGames);
        }
    }

    public static string GetSettingsFolder() => DefaultSettingsFolder;

    private void MigrateSettings()
    {
        Settings.NexusGameMappings ??= new Dictionary<string, NexusGameMapping>();
        Settings.SchemaVersion = CurrentSchemaVersion;
    }

    private static string ResolveDefaultSettingsFolder()
    {
        string? overridePath = Environment.GetEnvironmentVariable("RPGMODDER_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RPGModder");
    }

    private static void ProtectPath(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            File.SetUnixFileMode(path, isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some network filesystems do not expose Unix modes; persistence can still continue.
        }
    }
}

public class AppSettings
{
    public int SchemaVersion { get; set; } = SettingsService.CurrentSchemaVersion;
    public string? LastGamePath { get; set; }
    public bool AutoScanOnStartup { get; set; } = false;
    public DateTime LastScanTime { get; set; }
    
    // Nexus Mods settings
    public string? NexusApiKey { get; set; }
    public bool IsSsoAuthenticated { get; set; } = false;
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
