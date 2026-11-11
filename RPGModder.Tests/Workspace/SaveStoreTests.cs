using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

public sealed class SaveStoreTests : IDisposable
{
    private static readonly DateTimeOffset LocalNow = new(2026, 11, 11, 15, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly RpgGame _game;
    private readonly GameWorkspacePaths _workspace;
    private readonly SaveStore _store;

    public SaveStoreTests()
    {
        _game = new RpgProjectBuilder(_temp.Combine("game")).WithSaveFile("file1.rmmzsave", "live").Build();
        _workspace = new GameWorkspacePaths(_game.GameRoot);
        _workspace.EnsureCreated();
        _store = new SaveStore(_workspace, GameLayout.Detect(_game.GameRoot), new FileSystemOps());
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void BackupFile_StampsTheCopyWithTheLocalTime()
    {
        string backup = _store.BackupFile(Path.Combine(_game.SaveFolder, "file1.rmmzsave"), LocalNow);

        Assert.Equal(Path.Combine(_workspace.SaveBackups, "file1.rmmzsave_1111_1530.bak"), backup);
        Assert.Equal("live", File.ReadAllText(backup));
    }

    [Fact]
    public void RestoreFile_KeepsTheReplacedSaveFirst()
    {
        string save = Path.Combine(_game.SaveFolder, "file1.rmmzsave");
        string backup = Path.Combine(_workspace.SaveBackups, "older.bak");
        File.WriteAllText(backup, "older");

        _store.RestoreFile(save, backup, LocalNow);

        Assert.Equal("older", File.ReadAllText(save));
        Assert.Equal("live", File.ReadAllText(Path.Combine(_workspace.SaveBackups, "file1.rmmzsave_PRE_RESTORE_1111_1530.bak")));
    }

    [Fact]
    public void RestoreFile_RefusesABackupOutsideTheWorkspace()
    {
        string save = Path.Combine(_game.SaveFolder, "file1.rmmzsave");
        string outside = _temp.Combine("outside.bak");
        File.WriteAllText(outside, "outside");

        Assert.Throws<InvalidDataException>(() => _store.RestoreFile(save, outside, LocalNow));
        Assert.Equal("live", File.ReadAllText(save));
    }
}
