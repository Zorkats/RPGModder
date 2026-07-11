using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Tests;

public sealed class SafetyAndTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderSafetyTests_{Guid.NewGuid():N}");

    public SafetyAndTransactionTests()
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

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("C:\\outside.txt")]
    public void ResolveContainedPath_RejectsEscapingPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            SafePathService.ResolveContainedPath(_root, path, "Test path"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("folder/name")]
    [InlineData("folder\\name")]
    public void ValidateDirectoryName_RejectsUnsafeNames(string name)
    {
        Assert.Throws<InvalidDataException>(() =>
            SafePathService.ValidateDirectoryName(name, "Test name"));
    }

    [Fact]
    public void DeploymentTransaction_RestoresPreviousTreeWhenNotCommitted()
    {
        string content = Path.Combine(_root, "game");
        string data = Path.Combine(content, "data");
        string transactions = Path.Combine(_root, "transactions");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Actors.json"), "original");

        using (DeploymentTransaction.Begin(content, transactions, new[] { "data" }, Array.Empty<string>()))
        {
            File.WriteAllText(Path.Combine(data, "Actors.json"), "modified");
            File.WriteAllText(Path.Combine(data, "Added.json"), "added");
        }

        Assert.Equal("original", File.ReadAllText(Path.Combine(data, "Actors.json")));
        Assert.False(File.Exists(Path.Combine(data, "Added.json")));
    }

    [Fact]
    public void DeploymentTransaction_KeepsTreeWhenCommitted()
    {
        string content = Path.Combine(_root, "game");
        string data = Path.Combine(content, "data");
        string transactions = Path.Combine(_root, "transactions");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Actors.json"), "original");

        using var transaction = DeploymentTransaction.Begin(
            content,
            transactions,
            new[] { "data" },
            Array.Empty<string>());
        File.WriteAllText(Path.Combine(data, "Actors.json"), "modified");
        transaction.Commit();

        Assert.Equal("modified", File.ReadAllText(Path.Combine(data, "Actors.json")));
        Assert.Empty(Directory.GetDirectories(transactions));
    }

    [Fact]
    public void RecoverInterrupted_DiscardsIncompleteSnapshotWithoutChangingLiveTree()
    {
        string content = Path.Combine(_root, "game");
        string data = Path.Combine(content, "data");
        string transactions = Path.Combine(_root, "transactions");
        string interrupted = Path.Combine(transactions, "deployment-interrupted");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(interrupted, "snapshot", "data"));
        File.WriteAllText(Path.Combine(data, "Actors.json"), "live");
        File.WriteAllText(Path.Combine(interrupted, "snapshot", "data", "Actors.json"), "partial");
        File.WriteAllText(Path.Combine(interrupted, "transaction.json"), "{\"State\":\"preparing\"}");

        IReadOnlyList<string> recovered = DeploymentTransaction.RecoverInterrupted(
            content,
            transactions,
            new[] { "data" },
            Array.Empty<string>());

        Assert.Empty(recovered);
        Assert.Equal("live", File.ReadAllText(Path.Combine(data, "Actors.json")));
        Assert.False(Directory.Exists(interrupted));
    }

    [Fact]
    public void InstallMod_RejectsUnsafeManifestIdWithoutDeletingOutsideData()
    {
        string source = CreateMod("../victim", "data/file.txt");
        string mods = Path.Combine(_root, "mods");
        string victim = Path.Combine(_root, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");

        InstallResult result = new ModInstallerService().InstallMod(source, mods);

        Assert.False(result.Success);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
    }

    [Fact]
    public void InstallMod_RejectsEscapingTargetPath()
    {
        string source = CreateMod("safe-mod", "../../outside.txt");
        string mods = Path.Combine(_root, "mods");

        InstallResult result = new ModInstallerService().InstallMod(source, mods);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(mods, "safe-mod")));
    }

    [Fact]
    public void InstallMod_StagesAndPromotesValidatedMod()
    {
        string source = CreateMod("safe-mod", "data/file.txt");
        string mods = Path.Combine(_root, "mods");

        InstallResult result = new ModInstallerService().InstallMod(source, mods);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(mods, "safe-mod", "mod.json")));
        Assert.True(File.Exists(Path.Combine(mods, "safe-mod", "payload.txt")));
    }

    private string CreateMod(string id, string target)
    {
        string source = Path.Combine(_root, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), "payload");
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Id = id, Name = id },
            FileOps = new List<FileOperation>
            {
                new() { Source = "payload.txt", Target = target }
            }
        };
        File.WriteAllText(
            Path.Combine(source, "mod.json"),
            JsonConvert.SerializeObject(manifest));
        return source;
    }
}
