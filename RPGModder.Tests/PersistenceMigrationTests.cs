using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Tests;

public sealed class PersistenceMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderPersistenceTests_{Guid.NewGuid():N}");

    public PersistenceMigrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void SettingsService_MigratesLegacySettingsAndWritesCurrentSchema()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{\"LastGamePath\":\"game.exe\",\"NexusGameMappings\":null}");

        var service = new SettingsService(_root);
        service.Save();
        var saved = JsonConvert.DeserializeObject<AppSettings>(
            File.ReadAllText(Path.Combine(_root, "settings.json")));

        Assert.Equal(SettingsService.CurrentSchemaVersion, saved!.SchemaVersion);
        Assert.NotNull(saved.NexusGameMappings);
        Assert.Equal("game.exe", saved.LastGamePath);
    }

    [Fact]
    public void WorkspaceMigration_ImportsLegacyDataWithoutDeletingSource()
    {
        string game = Path.Combine(_root, "game");
        string legacyMod = Path.Combine(game, "Mods", "example");
        Directory.CreateDirectory(legacyMod);
        File.WriteAllText(Path.Combine(legacyMod, "mod.json"), "{}");
        File.WriteAllText(
            Path.Combine(game, "profile.json"),
            JsonConvert.SerializeObject(new ModProfile { EnabledMods = new List<string> { "example" } }));

        var paths = new GameWorkspacePaths(game);
        new WorkspaceMigrationService().ImportLegacyLayout(paths);

        Assert.True(File.Exists(Path.Combine(paths.Mods, "example", "mod.json")));
        Assert.True(File.Exists(paths.GetProfilePath("Default")));
        Assert.True(File.Exists(Path.Combine(legacyMod, "mod.json")));
        Assert.True(File.Exists(paths.SchemaMarker));

        Directory.Delete(Path.Combine(paths.Mods, "example"), true);
        new WorkspaceMigrationService().ImportLegacyLayout(paths);
        Assert.False(Directory.Exists(Path.Combine(paths.Mods, "example")));
    }

    [Fact]
    public void WorkspaceMigration_ResumesAfterPartialLegacyImport()
    {
        string game = Path.Combine(_root, "partial-game");
        string legacyMods = Path.Combine(game, "Mods");
        string legacyBackup = Path.Combine(game, "ModManager_Backups", "Clean_Vanilla", "data");
        Directory.CreateDirectory(Path.Combine(legacyMods, "already-imported"));
        Directory.CreateDirectory(Path.Combine(legacyMods, "missing-mod"));
        Directory.CreateDirectory(legacyBackup);
        File.WriteAllText(Path.Combine(legacyMods, "already-imported", "mod.json"), "{}");
        File.WriteAllText(Path.Combine(legacyMods, "missing-mod", "mod.json"), "{}");
        File.WriteAllText(Path.Combine(legacyBackup, "Actors.json"), "vanilla");
        File.WriteAllText(Path.Combine(game, "profile.json"), "{}");
        File.WriteAllText(Path.Combine(game, "mod_profile_Legacy.json"), "{}");

        var paths = new GameWorkspacePaths(game);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(paths.Mods, "already-imported"));
        File.WriteAllText(Path.Combine(paths.Mods, "already-imported", "mod.json"), "new-copy");

        WorkspaceMigrationResult result = new WorkspaceMigrationService().ImportLegacyLayout(paths);

        Assert.Equal(1, result.ImportedMods);
        Assert.Equal(2, result.ImportedProfiles);
        Assert.Equal("new-copy", File.ReadAllText(Path.Combine(paths.Mods, "already-imported", "mod.json")));
        Assert.True(File.Exists(Path.Combine(paths.Mods, "missing-mod", "mod.json")));
        Assert.True(File.Exists(Path.Combine(paths.CleanBackup, "data", "Actors.json")));
        Assert.True(File.Exists(paths.GetProfilePath("Default")));
        Assert.True(File.Exists(paths.GetProfilePath("Legacy")));
    }

    [Fact]
    public void WorkspaceMigration_UpgradesVersionTwoMarkerAndRetriesCompatibilityImport()
    {
        string game = Path.Combine(_root, "version-two-game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "profile.json"), "{}");
        var paths = new GameWorkspacePaths(game);
        paths.EnsureCreated();
        File.WriteAllText(paths.SchemaMarker, "{\"schemaVersion\":2}");

        WorkspaceMigrationResult result = new WorkspaceMigrationService().ImportLegacyLayout(paths);

        Assert.True(result.SchemaUpgraded);
        Assert.Equal(1, result.ImportedProfiles);
        Assert.True(File.Exists(paths.GetProfilePath("Default")));
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(paths.SchemaMarker));
    }

    [Fact]
    public void ProfileRepository_RejectsTraversalAndPersistsAtomically()
    {
        string game = Path.Combine(_root, "game");
        var paths = new GameWorkspacePaths(game);
        var repository = new RPGModder.Core.Application.ProfileRepository();

        repository.Save(paths, "Default", new ModProfile { EnabledMods = new List<string> { "example" } });

        Assert.Equal("example", Assert.Single(repository.Load(paths, "Default").EnabledMods));
        Assert.Throws<InvalidDataException>(() => repository.Save(paths, "../outside", new ModProfile()));
    }
}
