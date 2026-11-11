using RPGModder.Core.Services;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

public sealed class GameLayoutTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void WwwGame_UsesTheWwwFolderForContentAndSaves()
    {
        RpgGame game = new RpgProjectBuilder(_temp.Combine("mv")).WithEngine(RpgEngine.MV).WithWwwLayout().Build();

        GameLayout layout = GameLayout.Detect(game.GameRoot);

        Assert.True(layout.UsesWww);
        Assert.Equal(RpgMakerEngine.MV, layout.Engine);
        Assert.Equal(game.ContentRoot, layout.ContentRoot);
        Assert.Equal(Path.Combine(game.ContentRoot, "save"), layout.SaveFolder);
    }

    [Fact]
    public void RootGame_UsesTheGameRoot()
    {
        RpgGame game = new RpgProjectBuilder(_temp.Combine("mz")).WithEngine(RpgEngine.MZ).Build();

        GameLayout layout = GameLayout.Detect(game.GameRoot);

        Assert.False(layout.UsesWww);
        Assert.Equal(RpgMakerEngine.MZ, layout.Engine);
        Assert.Equal(game.GameRoot, layout.ContentRoot);
    }

    [Fact]
    public void WwwGame_WithOnlyARootSaveFolder_UsesTheRootSaveFolder()
    {
        RpgGame game = new RpgProjectBuilder(_temp.Combine("mv")).WithWwwLayout().Build();
        Directory.CreateDirectory(Path.Combine(game.GameRoot, "save"));

        GameLayout layout = GameLayout.Detect(game.GameRoot);

        Assert.Equal(Path.Combine(game.GameRoot, "save"), layout.SaveFolder);
    }

    [Fact]
    public void FolderWithoutAnEngineScript_HasNoEngine()
    {
        string root = _temp.Combine("empty");
        Directory.CreateDirectory(Path.Combine(root, "js"));

        GameLayout layout = GameLayout.Detect(root);

        Assert.Null(layout.Engine);
        Assert.False(layout.UsesWww);
    }
}
