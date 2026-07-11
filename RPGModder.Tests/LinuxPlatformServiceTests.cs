using RPGModder.Core.Services;
using System.Formats.Tar;
using System.IO.Compression;

namespace RPGModder.Tests;

public sealed class LinuxPlatformServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RPGModderPlatformTests", Guid.NewGuid().ToString("N"));

    public LinuxPlatformServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DetectorFindsProtonGameWithWwwLayout()
    {
        string gameRoot = Path.Combine(_root, "ProtonGame");
        Directory.CreateDirectory(Path.Combine(gameRoot, "www", "js"));
        File.WriteAllText(Path.Combine(gameRoot, "Game.exe"), "fixture");
        File.WriteAllText(Path.Combine(gameRoot, "www", "js", "rpg_core.js"), "fixture");
        File.WriteAllText(Path.Combine(gameRoot, "package.json"), "{\"name\":\"Portable MV Game\"}");

        DetectedGame? game = new GameDetectorService().DetectGameAt(gameRoot);

        Assert.NotNull(game);
        Assert.Equal("Portable MV Game", game.Name);
        Assert.Equal(RpgMakerEngine.MV, game.Engine);
        Assert.Equal(Path.Combine(gameRoot, "Game.exe"), game.ExePath);
    }

    [Fact]
    public void DetectorRequiresEngineSignature()
    {
        string gameRoot = Path.Combine(_root, "NotAGame");
        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(Path.Combine(gameRoot, "Game.exe"), "fixture");

        Assert.Null(new GameDetectorService().DetectGameAt(gameRoot));
    }

    [Fact]
    public void DetectorFindsNativeLinuxExecutable()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string gameRoot = Path.Combine(_root, "NativeGame");
        Directory.CreateDirectory(Path.Combine(gameRoot, "js"));
        string executable = Path.Combine(gameRoot, "Game");
        File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(gameRoot, "js", "rmmz_core.js"), "fixture");

        DetectedGame? game = new GameDetectorService().DetectGameAt(gameRoot);

        Assert.NotNull(game);
        Assert.Equal(RpgMakerEngine.MZ, game.Engine);
        Assert.Equal(executable, game.ExePath);
    }

    [Fact]
    public void SteamAppIdIsReadFromAdjacentManifest()
    {
        string steamApps = Path.Combine(_root, "SteamLibrary", "steamapps");
        string gameRoot = Path.Combine(steamApps, "common", "Manifest Game");
        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_1234.acf"),
            "\"AppState\"\n{\n\"appid\" \"1234\"\n\"installdir\" \"Manifest Game\"\n}");

        Assert.Equal(1234, new SteamLaunchService().DetectSteamAppId(gameRoot));
    }

    [Fact]
    public void NexusSsoUsesInjectedCredentialStore()
    {
        var store = new FakeCredentialStore();
        var service = new NexusSsoService("test", store);

        string reference = service.EncryptKeyForStorage("secret-value");

        Assert.Equal("fake:reference", reference);
        Assert.Equal("secret-value", service.DecryptKeyFromStorage(reference));
        Assert.True(service.IsSecureStoredValue(reference));
    }

    [Fact]
    public void NxmParserAcceptsCanonicalDownloadLink()
    {
        const string url = "nxm://lookoutside/mods/42/files/99?key=abc&expires=123&user_id=7";

        NxmLink? link = NxmProtocolHandler.ParseNxmUrl(url);

        Assert.NotNull(link);
        Assert.True(link.IsValid);
        Assert.True(link.HasDownloadParams);
        Assert.Equal("lookoutside", link.GameDomain);
        Assert.Equal(42, link.ModId);
        Assert.Equal(99, link.FileId);
    }

    [Fact]
    public void LinuxTarGzipUpdatePackageCanBeExtracted()
    {
        string source = Path.Combine(_root, "package-source");
        string destination = Path.Combine(_root, "package-destination");
        string tarPath = Path.Combine(_root, "update.tar");
        string archivePath = Path.Combine(_root, "update.tar.gz");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "RPGModder"), "linux-binary");
        TarFile.CreateFromDirectory(source, tarPath, includeBaseDirectory: false);
        using (var tar = File.OpenRead(tarPath))
        using (var archive = File.Create(archivePath))
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize))
            tar.CopyTo(gzip);

        UpdateService.ExtractPackage(archivePath, destination);

        Assert.Equal("linux-binary", File.ReadAllText(Path.Combine(destination, "RPGModder")));
    }

    [Fact]
    public void LinuxTarGzipUpdateCannotEscapeDestination()
    {
        string destination = Path.Combine(_root, "safe-destination");
        string archivePath = Path.Combine(_root, "malicious.tar.gz");
        Directory.CreateDirectory(destination);
        using (var archive = File.Create(archivePath))
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize))
        using (var writer = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "../escaped-file")
            {
                DataStream = new MemoryStream("unsafe"u8.ToArray())
            };
            writer.WriteEntry(entry);
        }

        try
        {
            UpdateService.ExtractPackage(archivePath, destination);
        }
        catch (IOException) { }
        catch (InvalidDataException) { }

        Assert.False(File.Exists(Path.Combine(_root, "escaped-file")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class FakeCredentialStore : ICredentialStoreService
    {
        private string _secret = "";
        public bool IsAvailable => true;
        public string Store(string rawSecret) { _secret = rawSecret; return "fake:reference"; }
        public string Retrieve(string storedValue) => storedValue == "fake:reference" ? _secret : "";
        public void Delete(string? storedValue = null) => _secret = "";
        public bool IsSecureValue(string storedValue) => storedValue == "fake:reference";
    }
}
