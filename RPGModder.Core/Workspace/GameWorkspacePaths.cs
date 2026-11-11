using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Every path under <c>.rpgmodder</c>, and the legacy 1.x paths the migration imports from (LAY-4).
/// The session builds exactly one instance per open game and passes it down (OWN-1).
/// </summary>
public sealed class GameWorkspacePaths
{
    /// <summary>Schema 4 replaces <c>backup_complete.marker</c> with <c>vanilla.manifest.json</c> (GR-V6).</summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>The version the 1.x layout import records; the vanilla migration then raises it to <see cref="CurrentSchemaVersion"/>.</summary>
    public const int LegacyImportSchemaVersion = 3;
    public const string LegacyProfilePrefix = "mod_profile_";

    public string GameRoot { get; }
    public string Root { get; }
    public string Mods { get; }
    public string Profiles { get; }
    public string Backups { get; }
    public string CleanBackup { get; }
    public string VanillaManifest { get; }
    public string VanillaStaging { get; }
    public string VanillaRetired { get; }
    public string LegacyBackupExtras { get; }
    public string SaveBackups { get; }
    public string ProfileSaves { get; }
    public string Transactions { get; }
    public string History { get; }
    public string ActiveProfileMarker { get; }
    public string SchemaMarker { get; }
    public string OperationLock { get; }
    public string Trash { get; }
    public string DeployedState { get; }

    public string LegacyMods { get; }
    public string LegacyCleanBackup { get; }
    public string LegacySaveBackups { get; }
    public string LegacyProfileSaves { get; }
    public string LegacyDefaultProfile { get; }
    public string LegacyActiveProfileMarker { get; }

    public GameWorkspacePaths(string gameRoot)
    {
        GameRoot = Path.GetFullPath(gameRoot);
        Root = Path.Combine(GameRoot, ".rpgmodder");
        Mods = Path.Combine(Root, "mods");
        Profiles = Path.Combine(Root, "profiles");
        Backups = Path.Combine(Root, "backups");
        CleanBackup = Path.Combine(Backups, "vanilla");
        VanillaManifest = Path.Combine(Backups, "vanilla.manifest.json");
        VanillaStaging = Path.Combine(Backups, "vanilla.staging");
        VanillaRetired = Path.Combine(Backups, "vanilla.retired");
        LegacyBackupExtras = Path.Combine(Root, "legacy", "backup-extras");
        SaveBackups = Path.Combine(Backups, "saves");
        ProfileSaves = Path.Combine(Backups, "profile-saves");
        Transactions = Path.Combine(Root, "transactions");
        History = Path.Combine(Root, "history");
        ActiveProfileMarker = Path.Combine(Profiles, "active-profile.txt");
        SchemaMarker = Path.Combine(Root, "workspace.json");
        OperationLock = Path.Combine(Root, "operation.lock");
        Trash = Path.Combine(Root, "trash");
        DeployedState = Path.Combine(Root, "deployed.json");

        string legacyBackups = Path.Combine(GameRoot, "ModManager_Backups");
        LegacyMods = Path.Combine(GameRoot, "Mods");
        LegacyCleanBackup = Path.Combine(legacyBackups, "Clean_Vanilla");
        LegacySaveBackups = Path.Combine(legacyBackups, "Saves");
        LegacyProfileSaves = Path.Combine(legacyBackups, "ProfileSaves");
        LegacyDefaultProfile = Path.Combine(GameRoot, "profile.json");
        LegacyActiveProfileMarker = Path.Combine(legacyBackups, "active_profile.txt");
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

    /// <summary>The 1.x named profiles, <c>mod_profile_&lt;name&gt;.json</c> next to the game.</summary>
    public IReadOnlyList<string> LegacyNamedProfiles()
    {
        return Directory.GetFiles(GameRoot, LegacyProfilePrefix + "*.json");
    }
}
