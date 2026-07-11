using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Tests;

public sealed class ModEngineTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderEngineTests_{Guid.NewGuid():N}");

    public ModEngineTransactionTests()
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
    public void RebuildGame_RestoresPreviousStateWhenModApplicationFails()
    {
        string game = Path.Combine(_root, "game");
        string data = Path.Combine(game, "data");
        Directory.CreateDirectory(data);
        string originalFile = Path.Combine(data, "Actors.json");
        File.WriteAllText(originalFile, "vanilla");
        string executable = Path.Combine(game, "Game.exe");
        File.WriteAllText(executable, "test");

        var engine = new ModEngine(executable);
        engine.InitializeSafeState();
        var workspace = new GameWorkspacePaths(game);
        string mod = Path.Combine(workspace.Mods, "broken-mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "replacement.json"), "replacement");
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Id = "broken-mod", Name = "Broken mod" },
            FileOps = new List<FileOperation>
            {
                new() { Source = "replacement.json", Target = "data/Actors.json" },
                new() { Source = "missing.json", Target = "data/Missing.json" }
            }
        };
        File.WriteAllText(Path.Combine(mod, "mod.json"), JsonConvert.SerializeObject(manifest));

        OperationResult result = engine.RebuildGame(new ModProfile
        {
            EnabledMods = new List<string> { "broken-mod" },
            LoadOrder = new List<string> { "broken-mod" }
        });

        Assert.False(result.Success);
        Assert.Equal("vanilla", File.ReadAllText(originalFile));
        Assert.False(File.Exists(Path.Combine(data, "Missing.json")));
        Assert.Single(Directory.GetFiles(workspace.History, "deployment-*.json"));
    }
}
