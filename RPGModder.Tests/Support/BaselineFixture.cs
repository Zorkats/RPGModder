using RPGModder.Core.Models;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;

namespace RPGModder.Tests.Support;

/// <summary>
/// A game installed in a synthetic Steam library, with its workspace, for the vanilla baseline tests
/// (TST-11, TST-17, TST-19). The store manifests are written by the test when it wants them.
/// </summary>
public sealed class BaselineFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    public BaselineFixture(Func<RpgProjectBuilder, RpgProjectBuilder>? configure = null)
    {
        Library = new SyntheticSteamLibrary(_temp.Combine("library"));
        var builder = new RpgProjectBuilder(Library.InstallFolder)
            .WithContentFile("img/pictures/title.png", "title picture")
            .WithContentFile("audio/bgm/theme.ogg", "theme music")
            .WithSaveFile("file1.rmmzsave", "save");
        Game = (configure?.Invoke(builder) ?? builder).Build();
        Workspace = new GameWorkspacePaths(Game.GameRoot);
        Workspace.EnsureCreated();
        Layout = GameLayout.Detect(Game.GameRoot);
    }

    public SyntheticSteamLibrary Library { get; }

    public RpgGame Game { get; }

    public GameWorkspacePaths Workspace { get; }

    public GameLayout Layout { get; }

    public VanillaStore Vanilla => new(Workspace, Layout, new FileSystemOps());

    /// <summary>Writes Steam's app manifest and the depot manifest of the install as it stands now.</summary>
    public void PublishStoreManifest(string buildId = SyntheticSteamLibrary.BuildId)
    {
        Library.WriteAppManifest(buildId);
        Library.WriteDepotManifestFromInstall();
    }

    public IStoreManifestSource Steam(IFileSystemOps files)
    {
        return Library.Reader(files);
    }

    public VanillaBaselineBuilder Builder(IFileSystemOps files, IStoreManifestSource store, TimeProvider? clock = null)
    {
        return new VanillaBaselineBuilder(Workspace, Layout, files, store, clock ?? TimeProvider.System);
    }

    public VanillaBaselineMigration Migration(IFileSystemOps files, IStoreManifestSource store, TimeProvider? clock = null)
    {
        return new VanillaBaselineMigration(Workspace, Layout, files, store, clock ?? TimeProvider.System);
    }

    /// <summary>Installs a mod that replaces <paramref name="target"/>; returns the path of its copy of the file.</summary>
    public string InstallMod(string id, string target, string content)
    {
        string folder = new ModBuilder(id).ReplaceFile(target, content).BuildInto(Workspace.Mods);
        return Path.Combine(folder, "files", target.Replace('/', Path.DirectorySeparatorChar));
    }

    public void WriteLive(string relativePath, string content)
    {
        string path = Game.ContentFile(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// The invariant of every interrupted build: either no manifest, so nothing is trusted, or a
    /// manifest whose files are exactly the backup's files with the recorded hashes.
    /// </summary>
    public void AssertNoBackupOrConsistent()
    {
        VanillaStore vanilla = Vanilla;
        VanillaManifest? manifest = vanilla.ReadManifest();
        if (manifest == null)
        {
            return;
        }

        Assert.Equal(manifest.Files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal), vanilla.Files);
        foreach (VanillaFileEntry file in manifest.Files)
        {
            Assert.Equal(file.Sha256, vanilla.Hash(file.Path));
        }
    }

    public void Dispose()
    {
        _temp.Dispose();
    }
}
