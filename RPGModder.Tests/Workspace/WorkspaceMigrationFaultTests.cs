using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

/// <summary>The 1.x layout import, failed at every file call and then run again (GR-V8, TST-9).</summary>
public sealed class WorkspaceMigrationFaultTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Import_FailedAtAnyCall_CompletesOnTheNextOpen_AndKeepsTheSource()
    {
        int calls = CountImportCalls();
        Assert.True(calls >= 4, $"The legacy import made {calls} file calls.");

        for (int failAt = 1; failAt <= calls; failAt++)
        {
            GameWorkspacePaths paths = LegacyGame($"game-{failAt}");
            var faulty = new FaultInjectingFileOps(failAt);

            Assert.Throws<IOException>(() => new WorkspaceMigrationService(faulty).ImportLegacyLayout(paths));
            Assert.False(File.Exists(paths.SchemaMarker) && failAt < calls, $"The schema marker was written before call {failAt} failed.");

            new WorkspaceMigrationService().ImportLegacyLayout(paths);

            Assert.Equal("{\"name\":\"legacy\"}", File.ReadAllText(Path.Combine(paths.Mods, "LegacyMod", "mod.json")));
            Assert.Equal("vanilla", File.ReadAllText(Path.Combine(paths.CleanBackup, "data", "System.json")));
            Assert.Equal("{\"EnabledMods\":[\"LegacyMod\"]}", File.ReadAllText(paths.GetProfilePath("Default")));
            Assert.Equal("{\"EnabledMods\":[]}", File.ReadAllText(paths.GetProfilePath("Second")));
            Assert.True(File.Exists(paths.SchemaMarker));
            Assert.True(File.Exists(Path.Combine(paths.LegacyMods, "LegacyMod", "mod.json")));
            Assert.True(File.Exists(paths.LegacyDefaultProfile));
        }
    }

    private int CountImportCalls()
    {
        var counter = new FaultInjectingFileOps();
        new WorkspaceMigrationService(counter).ImportLegacyLayout(LegacyGame("game-count"));
        return counter.Calls;
    }

    // A 1.x game folder: the library, the vanilla backup and two profiles next to the game.
    private GameWorkspacePaths LegacyGame(string name)
    {
        var paths = new GameWorkspacePaths(_temp.Combine(name));
        Write(Path.Combine(paths.LegacyMods, "LegacyMod", "mod.json"), "{\"name\":\"legacy\"}");
        Write(Path.Combine(paths.LegacyCleanBackup, "data", "System.json"), "vanilla");
        Write(paths.LegacyDefaultProfile, "{\"EnabledMods\":[\"LegacyMod\"]}");
        Write(Path.Combine(paths.GameRoot, GameWorkspacePaths.LegacyProfilePrefix + "Second.json"), "{\"EnabledMods\":[]}");
        return paths;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
