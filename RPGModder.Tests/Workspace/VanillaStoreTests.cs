using System.Security.Cryptography;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

public sealed class VanillaStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RpgGame _game;
    private readonly GameWorkspacePaths _workspace;
    private readonly GameLayout _layout;

    public VanillaStoreTests()
    {
        _game = new RpgProjectBuilder(_temp.Combine("game"))
            .WithContentFile("img/pictures/title.png", "picture")
            .WithSaveFile("file1.rmmzsave", "save")
            .Build();
        _workspace = new GameWorkspacePaths(_game.GameRoot);
        _layout = GameLayout.Detect(_game.GameRoot);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void EnsureBackup_CopiesManagedContent_AndListsItWithoutTheMarker()
    {
        var store = new VanillaStore(_workspace, _layout, new FileSystemOps());

        store.EnsureBackup();

        Assert.True(store.IsComplete);
        Assert.Contains("data/System.json", store.Files);
        Assert.Contains("img/pictures/title.png", store.Files);
        Assert.Contains("index.html", store.Files);
        Assert.DoesNotContain(VanillaStore.MarkerFileName, store.Files);
        Assert.DoesNotContain(store.Files, file => file.StartsWith("save/", StringComparison.Ordinal));
        Assert.Equal(store.Files.OrderBy(file => file, StringComparer.Ordinal), store.Files);
        Assert.Equal("picture", store.ReadText("img/pictures/title.png"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_game.ContentFile("data/System.json")))),
            store.Hash("data/System.json"));
    }

    [Fact]
    public void Discard_InterruptedAfterTheMarker_LeavesNoTrustedBackup()
    {
        new VanillaStore(_workspace, _layout, new FileSystemOps()).EnsureBackup();
        var faulty = new VanillaStore(_workspace, _layout, new FaultInjectingFileOps(failAt: 2));

        Assert.Throws<IOException>(() => faulty.Discard());

        Assert.False(faulty.IsComplete);
        Assert.True(Directory.Exists(faulty.Root));

        var store = new VanillaStore(_workspace, _layout, new FileSystemOps());
        store.EnsureBackup();
        Assert.True(store.IsComplete);
    }

    [Fact]
    public void PathOf_RefusesAPathOutsideTheBackup()
    {
        var store = new VanillaStore(_workspace, _layout, new FileSystemOps());

        Assert.Throws<InvalidDataException>(() => store.PathOf("../outside.txt"));
    }
}
