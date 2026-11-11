using System.Security.Cryptography;
using RPGModder.Core.Models;
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
    public void Build_CopiesManagedContent_AndListsItWithoutTheMarker()
    {
        var store = new VanillaStore(_workspace, _layout, new FileSystemOps());

        Build(new FileSystemOps());

        Assert.True(store.IsComplete);
        Assert.Equal(VanillaBaseline.Confirmed, store.ReadManifest()!.Baseline);
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
        Build(new FileSystemOps());
        var faulty = new VanillaStore(_workspace, _layout, new FaultInjectingFileOps(failAt: 2));

        Assert.Throws<IOException>(() => faulty.Discard());

        Assert.False(faulty.IsComplete);
        Assert.True(Directory.Exists(faulty.Root));

        Build(new FileSystemOps());
        Assert.True(new VanillaStore(_workspace, _layout, new FileSystemOps()).IsComplete);
    }

    [Fact]
    public void PathOf_RefusesAPathOutsideTheBackup()
    {
        var store = new VanillaStore(_workspace, _layout, new FileSystemOps());

        Assert.Throws<InvalidDataException>(() => store.PathOf("../outside.txt"));
    }

    private void Build(IFileSystemOps files)
    {
        BaselineOutcome outcome = new VanillaBaselineBuilder(_workspace, _layout, files, new NoStoreManifestSource(), TimeProvider.System)
            .Build(BaselineAnswers.None);
        Assert.True(outcome.Built);
    }
}
