using RPGModder.Core.Models;
using RPGModder.Core.Services;
using Newtonsoft.Json;

namespace RPGModder.Tests;

public class ModPackerServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ModPackerService _packer;

    public ModPackerServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"RPGModderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _packer = new ModPackerService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    private string CreateTestFolder(string name)
    {
        string path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void CreateFile(string folder, string relativePath, string content)
    {
        string fullPath = Path.Combine(folder, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public void AnalyzeDifferences_DetectsNewFiles()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla");
        string work = CreateTestFolder("work");

        CreateFile(vanilla, "data/System.json", "{}");
        CreateFile(work, "data/System.json", "{}");
        CreateFile(work, "img/pictures/NewImage.png", "PNG_DATA");

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.NewFiles);
        Assert.Contains("img/pictures/NewImage.png", result.NewFiles.Keys);
    }

    [Fact]
    public void AnalyzeDifferences_DetectsModifiedFiles()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla2");
        string work = CreateTestFolder("work2");

        CreateFile(vanilla, "img/pictures/Title.png", "ORIGINAL");
        CreateFile(work, "img/pictures/Title.png", "MODIFIED_CONTENT");

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.ModifiedFiles);
        Assert.Contains("img/pictures/Title.png", result.ModifiedFiles.Keys);
    }

    [Fact]
    public void AnalyzeDifferences_DetectsJsonPatch()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla3");
        string work = CreateTestFolder("work3");

        var vanillaSystem = new { gameTitle = "Original", screenWidth = 816, screenHeight = 624 };
        var workSystem = new { gameTitle = "Original", screenWidth = 1920, screenHeight = 1080 };

        CreateFile(vanilla, "data/System.json", JsonConvert.SerializeObject(vanillaSystem));
        CreateFile(work, "data/System.json", JsonConvert.SerializeObject(workSystem));

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.JsonPatches);
        Assert.Contains("data/System.json", result.JsonPatches.Keys);

        var patch = result.JsonPatches["data/System.json"];
        Assert.Equal(1920, patch["screenWidth"]?.ToObject<int>());
        Assert.Equal(1080, patch["screenHeight"]?.ToObject<int>());
        Assert.Null(patch["gameTitle"]); // Unchanged value should NOT be in patch
    }

    [Fact]
    public void AnalyzeDifferences_DetectsNewPlugins()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla4");
        string work = CreateTestFolder("work4");

        string vanillaPlugins = @"var $plugins = [
            {""name"":""BasePlugin"",""status"":true,""description"":""Vanilla plugin"",""parameters"":{}}
        ];";

        string workPlugins = @"var $plugins = [
            {""name"":""BasePlugin"",""status"":true,""description"":""Vanilla plugin"",""parameters"":{}},
            {""name"":""MyNewPlugin"",""status"":true,""description"":""Cool mod"",""parameters"":{}}
        ];";

        CreateFile(vanilla, "js/plugins.js", vanillaPlugins);
        CreateFile(work, "js/plugins.js", workPlugins);

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.NewPlugins);
        Assert.Equal("MyNewPlugin", result.NewPlugins[0].Name);
    }

    [Fact]
    public void AnalyzeDifferences_IgnoresSaveFolders()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla5");
        string work = CreateTestFolder("work5");

        CreateFile(vanilla, "data/System.json", "{}");
        CreateFile(work, "data/System.json", "{}");
        CreateFile(work, "save/file1.rpgsave", "SAVE_DATA"); // Should be ignored

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.NewFiles);
        Assert.Empty(result.ModifiedFiles);
    }

    [Fact]
    public void AnalyzeDifferences_ReportsEmptyOnIdenticalFolders()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla6");
        string work = CreateTestFolder("work6");

        CreateFile(vanilla, "data/System.json", "{\"title\":\"Test\"}");
        CreateFile(work, "data/System.json", "{\"title\":\"Test\"}");

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.TotalChanges);
    }

    [Fact]
    public void GenerateManifest_CreatesCorrectStructure()
    {
        // Arrange
        var analysis = new PackerResult
        {
            NewFiles = new Dictionary<string, string>
            {
                ["img/pictures/NewArt.png"] = "/fake/path/NewArt.png"
            },
            ModifiedFiles = new Dictionary<string, string>
            {
                ["audio/bgm/Battle1.ogg"] = "/fake/path/Battle1.ogg"
            }
        };

        var metadata = new ModMetadata
        {
            Name = "Test Mod",
            Author = "Tester",
            Version = "1.0",
            Description = "A test mod"
        };

        // Act
        var manifest = _packer.GenerateManifest(analysis, metadata);

        // Assert
        Assert.Equal("Test Mod", manifest.Metadata.Name);
        Assert.Equal(2, manifest.FileOps.Count);

        var newFileOp = manifest.FileOps.First(f => f.Target.Contains("NewArt"));
        Assert.Equal("img/pictures/NewArt.png", newFileOp.Source);
        Assert.Equal("img/pictures/NewArt.png", newFileOp.Target);
    }

    [Fact]
    public void CreateModPackage_CreatesValidStructure()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla7");
        string work = CreateTestFolder("work7");
        string output = CreateTestFolder("output7");

        CreateFile(vanilla, "data/System.json", "{}");
        CreateFile(work, "data/System.json", "{}");
        CreateFile(work, "img/test.png", "PNG_BYTES");

        var analysis = _packer.AnalyzeDifferences(work, vanilla);
        var metadata = new ModMetadata { Name = "PackageTest", Author = "Test", Version = "1.0" };
        var manifest = _packer.GenerateManifest(analysis, metadata);

        string modFolder = Path.Combine(output, "TestMod");

        // Act
        _packer.CreateModPackage(modFolder, manifest, analysis);

        // Assert
        Assert.True(File.Exists(Path.Combine(modFolder, "mod.json")));
        Assert.True(File.Exists(Path.Combine(modFolder, "img", "test.png")));

        var loadedManifest = JsonConvert.DeserializeObject<ModManifest>(
            File.ReadAllText(Path.Combine(modFolder, "mod.json")));
        Assert.NotNull(loadedManifest);
        Assert.Equal("PackageTest", loadedManifest.Metadata.Name);
    }

    [Fact]
    public void AnalyzeDifferences_HandlesNestedJsonChanges()
    {
        // Arrange
        string vanilla = CreateTestFolder("vanilla8");
        string work = CreateTestFolder("work8");

        var vanillaSystem = new
        {
            gameTitle = "Test",
            advanced = new { screenWidth = 816, screenHeight = 624 }
        };

        var workSystem = new
        {
            gameTitle = "Test",
            advanced = new { screenWidth = 1920, screenHeight = 1080 }
        };

        CreateFile(vanilla, "data/System.json", JsonConvert.SerializeObject(vanillaSystem));
        CreateFile(work, "data/System.json", JsonConvert.SerializeObject(workSystem));

        // Act
        var result = _packer.AnalyzeDifferences(work, vanilla);

        // Assert
        Assert.True(result.Success);
        Assert.Single(result.JsonPatches);

        var patch = result.JsonPatches["data/System.json"];
        var advanced = patch["advanced"] as Newtonsoft.Json.Linq.JObject;
        Assert.NotNull(advanced);
        Assert.Equal(1920, advanced["screenWidth"]?.ToObject<int>());
    }
}
