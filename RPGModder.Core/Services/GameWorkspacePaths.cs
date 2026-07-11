namespace RPGModder.Core.Services;

public sealed class GameWorkspacePaths
{
    public const int CurrentSchemaVersion = 3;

    public string GameRoot { get; }
    public string Root { get; }
    public string Mods { get; }
    public string Profiles { get; }
    public string Backups { get; }
    public string CleanBackup { get; }
    public string SaveBackups { get; }
    public string ProfileSaves { get; }
    public string Transactions { get; }
    public string History { get; }
    public string ActiveProfileMarker { get; }
    public string SchemaMarker { get; }

    public GameWorkspacePaths(string gameRoot)
    {
        GameRoot = Path.GetFullPath(gameRoot);
        Root = Path.Combine(GameRoot, ".rpgmodder");
        Mods = Path.Combine(Root, "mods");
        Profiles = Path.Combine(Root, "profiles");
        Backups = Path.Combine(Root, "backups");
        CleanBackup = Path.Combine(Backups, "vanilla");
        SaveBackups = Path.Combine(Backups, "saves");
        ProfileSaves = Path.Combine(Backups, "profile-saves");
        Transactions = Path.Combine(Root, "transactions");
        History = Path.Combine(Root, "history");
        ActiveProfileMarker = Path.Combine(Profiles, "active-profile.txt");
        SchemaMarker = Path.Combine(Root, "workspace.json");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(SaveBackups);
        Directory.CreateDirectory(ProfileSaves);
        Directory.CreateDirectory(Transactions);
        Directory.CreateDirectory(History);
    }

    public string GetProfilePath(string profileName)
    {
        string safeName = SafePathService.ValidateDirectoryName(profileName, "Profile name");
        string fileName = safeName.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? "default.json"
            : $"{safeName}.json";
        return SafePathService.ResolveContainedPath(Profiles, fileName, "Profile path");
    }
}
