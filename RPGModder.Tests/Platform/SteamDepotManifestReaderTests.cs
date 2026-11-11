using System.Security.Cryptography;
using RPGModder.Core.Platform;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Platform;

/// <summary>The store manifest reader (TST-19) on a synthetic Steam library; no test needs Steam.</summary>
public sealed class SteamDepotManifestReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SyntheticSteamLibrary _library;

    public SteamDepotManifestReaderTests()
    {
        _library = new SyntheticSteamLibrary(_temp.Combine("library"));
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Read_ListsEveryFileWithSizeAndSha1_AndSkipsFolders()
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder).WithContentFile("img/pictures/title.png", "picture"));

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.True(result.IsAvailable, result.Reason);
        StoreFileEntry title = Assert.IsType<StoreFileEntry>(result.Find("img/pictures/title.png"));
        byte[] bytes = File.ReadAllBytes(game.ContentFile("img/pictures/title.png"));
        Assert.Equal(bytes.LongLength, title.Size);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(bytes)), title.Sha1);
        Assert.Null(result.Find("img/pictures"));
        Assert.Null(result.Find("img"));
        Assert.Equal(SyntheticSteamLibrary.AppId, result.App!.AppId);
        Assert.Equal(SyntheticSteamLibrary.BuildId, result.App.BuildId);
        Assert.Equal(SyntheticSteamLibrary.ManifestId, Assert.Single(result.App.Depots).ManifestId);
    }

    [Fact]
    public void Read_MapsPathsToTheWwwContentRoot_AndSkipsFilesOutsideIt()
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder).WithWwwLayout());

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotNull(result.Find("data/System.json"));
        Assert.NotNull(result.Find("index.html"));
        Assert.DoesNotContain(result.Files, file => file.Path.Contains("Game.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, file => file.Path.StartsWith("www/", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_IsUnavailable_WhenTheDepotManifestIsNotCached()
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder));
        File.Delete(_library.DepotManifestPath);

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.False(result.IsAvailable);
        Assert.Equal(SyntheticSteamLibrary.AppId, result.App?.AppId);
    }

    [Fact]
    public void Read_IsUnavailable_WhenFileNamesAreEncrypted()
    {
        RpgGame game = new RpgProjectBuilder(_library.InstallFolder).Build();
        _library.WriteAppManifest();
        _library.WriteDepotManifestFromInstall(filenamesEncrypted: true);

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.False(result.IsAvailable);
        Assert.Contains("encrypted", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(-5)]
    public void Read_IsUnavailable_WhenTheDepotManifestIsTruncated(int cut)
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder));
        byte[] bytes = File.ReadAllBytes(_library.DepotManifestPath);
        int length = cut >= 0 ? cut : bytes.Length + cut;
        File.WriteAllBytes(_library.DepotManifestPath, bytes[..length]);

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void Read_IsUnavailable_WhenTheDepotManifestHasBytesAfterTheEnd()
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder));
        File.AppendAllText(_library.DepotManifestPath, "x");

        Assert.False(_library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot).IsAvailable);
    }

    [Fact]
    public void Read_IsUnavailable_WhenASectionIsUnknown()
    {
        RpgGame game = new RpgProjectBuilder(_library.InstallFolder).Build();
        _library.WriteAppManifest();
        _library.WriteDepotManifest(SyntheticDepotWriter.EntriesOf(_library.InstallFolder), extraSectionMagic: 0x12345678);

        Assert.False(_library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot).IsAvailable);
    }

    [Fact]
    public void Read_IsUnavailable_WhenTheDepotManifestNamesAnotherManifest()
    {
        RpgGame game = new RpgProjectBuilder(_library.InstallFolder).Build();
        _library.WriteAppManifest();
        File.WriteAllBytes(
            _library.DepotManifestPath,
            SyntheticDepotWriter.Build(ulong.Parse(SyntheticSteamLibrary.DepotId), 9000000000000000002, SyntheticDepotWriter.EntriesOf(_library.InstallFolder)));

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.False(result.IsAvailable);
        Assert.Contains("another", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_IsUnavailable_WhenADepotNameLeavesTheInstallFolder()
    {
        RpgGame game = new RpgProjectBuilder(_library.InstallFolder).Build();
        _library.WriteAppManifest();
        _library.WriteDepotManifest(new[] { new SyntheticDepotWriter.Entry("..\\outside.txt", 1, 0, new byte[20]) });

        Assert.False(_library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot).IsAvailable);
    }

    [Fact]
    public void Read_IsUnavailable_ForAGameOutsideASteamLibrary()
    {
        RpgGame game = new RpgProjectBuilder(_temp.Combine("elsewhere", "game")).Build();

        StoreManifestResult result = _library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot);

        Assert.False(result.IsAvailable);
        Assert.Equal(NoStoreManifestSource.NotAStoreInstall, result.Reason);
        Assert.Null(result.App);
    }

    [Fact]
    public void Read_IsUnavailable_WhenNoAppManifestNamesTheFolder()
    {
        RpgGame game = new RpgProjectBuilder(_library.InstallFolder).Build();

        Assert.False(_library.Reader(new FileSystemOps()).Read(game.GameRoot, game.ContentRoot).IsAvailable);
    }

    [Fact]
    public void Read_OpensOnlyTheAppAndDepotManifests_NeverAccountFiles()
    {
        RpgGame game = Install(new RpgProjectBuilder(_library.InstallFolder));
        Directory.CreateDirectory(Path.Combine(_library.Root, "config"));
        File.WriteAllText(Path.Combine(_library.Root, "config", "loginusers.vdf"), "\"users\" { }");
        File.WriteAllText(Path.Combine(_library.Root, "config", "config.vdf"), "\"InstallConfigStore\" { }");
        File.WriteAllText(Path.Combine(_library.Root, "ssfn0000000000000000000"), "x");
        Directory.CreateDirectory(Path.Combine(_library.Root, "userdata", "1"));
        var files = new FaultInjectingFileOps();

        StoreManifestResult result = _library.Reader(files).Read(game.GameRoot, game.ContentRoot);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotEmpty(files.ReadPaths);
        Assert.Equal(0, files.Calls);
        foreach (string path in files.ReadPaths)
        {
            string name = Path.GetFileName(path);
            bool allowed = path == _library.SteamApps ||
                           (Path.GetDirectoryName(path) == _library.SteamApps && name.StartsWith("appmanifest_", StringComparison.Ordinal) && name.EndsWith(".acf", StringComparison.Ordinal)) ||
                           (Path.GetDirectoryName(path) == _library.DepotCache && name.EndsWith(".manifest", StringComparison.Ordinal));
            Assert.True(allowed, $"The reader opened {path}.");
            Assert.DoesNotContain("loginusers", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ssfn", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("userdata", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine(_library.Root, "config"), path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RequestFetch_OpensOnlyTheValidateLinkOfTheApp()
    {
        var links = new RecordingLinkOpener();
        SteamDepotManifestReader reader = _library.Reader(new FileSystemOps(), links);

        Assert.True(reader.RequestFetch(SyntheticSteamLibrary.AppId));
        Assert.False(reader.RequestFetch("42; rm -rf"));
        Assert.False(reader.RequestFetch(string.Empty));

        Assert.Equal(new[] { "steam://validate/4242420" }, links.Opened);
    }

    private RpgGame Install(RpgProjectBuilder builder)
    {
        RpgGame game = builder.Build();
        _library.WriteAppManifest();
        _library.WriteDepotManifestFromInstall();
        return game;
    }
}
