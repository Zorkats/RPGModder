using Newtonsoft.Json.Linq;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using RPGModder.Core.Updates;
using RPGModder.Tests.Support;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RPGModder.Tests.Updates;

/// <summary>TX-13: the updater takes its package by exact name, verifies it against SHA256SUMS before staging, and extracts zips safely.</summary>
public sealed class UpdateVerificationTests : IDisposable
{
    private const string Version = "2.1.0";
    private const string Download = "https://github.com/Zorkats/RPGModder/releases/download/v2.1.0/";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectRelease_TakesThePackageByItsExactName(bool linux)
    {
        JObject release = Release(Version, "Source-win-linux.zip", "RPGModder-2.1.0-win-x64.zip.sig",
            ReleaseAssets.WindowsZip(Version), ReleaseAssets.LinuxZip(Version), ReleaseAssets.LinuxTarGz(Version), ReleaseAssets.Checksums);

        UpdateInfo? update = UpdateService.SelectRelease(release, "2.0.0", linux);

        Assert.NotNull(update);
        string expected = linux ? ReleaseAssets.LinuxTarGz(Version) : ReleaseAssets.WindowsZip(Version);
        Assert.Equal(expected, update.AssetName);
        Assert.Equal(Download + expected, update.DownloadUrl);
        Assert.Equal(Download + ReleaseAssets.Checksums, update.ChecksumsUrl);
        Assert.Equal(Version, update.Version);
    }

    [Fact]
    public void SelectRelease_OnLinux_FallsBackToTheZip()
    {
        JObject release = Release(Version, ReleaseAssets.WindowsZip(Version), ReleaseAssets.LinuxZip(Version), ReleaseAssets.Checksums);

        Assert.Equal(ReleaseAssets.LinuxZip(Version), UpdateService.SelectRelease(release, "2.0.0", linux: true)?.AssetName);
    }

    [Fact]
    public void SelectRelease_OffersNothing_WithoutChecksums()
    {
        JObject release = Release(Version, ReleaseAssets.WindowsZip(Version), ReleaseAssets.LinuxTarGz(Version));

        Assert.Null(UpdateService.SelectRelease(release, "2.0.0", linux: false));
        Assert.Null(UpdateService.SelectRelease(release, "2.0.0", linux: true));
    }

    [Fact]
    public void SelectRelease_OffersNothing_ForAnotherHost()
    {
        JObject release = Release(Version, ReleaseAssets.WindowsZip(Version), ReleaseAssets.Checksums);
        release["assets"]![0]!["browser_download_url"] = "https://example.com/" + ReleaseAssets.WindowsZip(Version);

        Assert.Null(UpdateService.SelectRelease(release, "2.0.0", linux: false));
    }

    [Fact]
    public void SelectRelease_OffersNothing_ForAPackageListedTwice()
    {
        JObject release = Release(Version, ReleaseAssets.WindowsZip(Version), ReleaseAssets.WindowsZip(Version), ReleaseAssets.Checksums);

        Assert.Null(UpdateService.SelectRelease(release, "2.0.0", linux: false));
    }

    [Theory]
    [InlineData("2.1.0")]
    [InlineData("2.2.0")]
    public void SelectRelease_OffersNothing_WhenNotNewer(string current)
    {
        JObject release = Release(Version, ReleaseAssets.WindowsZip(Version), ReleaseAssets.Checksums);

        Assert.Null(UpdateService.SelectRelease(release, current, linux: false));
    }

    [Fact]
    public void Checksums_Find_ReadsTheSha256sumFormat()
    {
        string text = Hash("a") + "  " + ReleaseAssets.WindowsZip(Version) + "\r\n" +
                      Hash("b") + " *" + ReleaseAssets.LinuxTarGz(Version) + "\n" +
                      "not a line\n";

        Assert.Equal(Hash("a"), ReleaseChecksums.Find(text, ReleaseAssets.WindowsZip(Version)));
        Assert.Equal(Hash("b"), ReleaseChecksums.Find(text, ReleaseAssets.LinuxTarGz(Version)));
        Assert.Null(ReleaseChecksums.Find(text, ReleaseAssets.LinuxZip(Version)));
        Assert.Null(ReleaseChecksums.Find(text, "RPGModder-2.1.0-win-x64"));
    }

    [Fact]
    public void Checksums_Find_RefusesANameListedTwice()
    {
        string name = ReleaseAssets.WindowsZip(Version);
        string text = $"{Hash("a")}  {name}\n{Hash("b")}  {name}\n";

        UpdateChecksumException error = Assert.Throws<UpdateChecksumException>(() => ReleaseChecksums.Find(text, name));
        Assert.Equal(DiagnosticCodes.UpdateChecksumFailed, error.Code);
    }

    [Fact]
    public void Checksums_Verify_AcceptsTheListedHash_AndRefusesAnother()
    {
        string name = ReleaseAssets.WindowsZip(Version);
        string file = _temp.Combine(name);
        File.WriteAllText(file, "package");

        ReleaseChecksums.Verify(file, $"{Hash("package").ToLowerInvariant()}  {name}\n", name);
        UpdateChecksumException wrong = Assert.Throws<UpdateChecksumException>(() => ReleaseChecksums.Verify(file, $"{Hash("other")}  {name}\n", name));
        UpdateChecksumException missing = Assert.Throws<UpdateChecksumException>(() => ReleaseChecksums.Verify(file, $"{Hash("package")}  other.zip\n", name));

        Assert.Equal(DiagnosticCodes.UpdateChecksumFailed, wrong.Code);
        Assert.Equal(DiagnosticCodes.UpdateChecksumFailed, missing.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_WithAWrongOrMissingHash_StagesNothing(bool listed)
    {
        UpdateInfo update = PlatformUpdate();
        string sums = listed ? $"{Hash("something else")}  {update.AssetName}\n" : $"{Hash("package")}  other.zip\n";
        using var service = new UpdateService(new FixedResponses(new Dictionary<string, byte[]>
        {
            [update.ChecksumsUrl] = Encoding.UTF8.GetBytes(sums),
            [update.DownloadUrl] = Encoding.UTF8.GetBytes("package")
        }));

        UpdateChecksumException error = await Assert.ThrowsAsync<UpdateChecksumException>(
            () => service.DownloadAndInstallAsync(update, new Progress<double>()));

        Assert.Equal(DiagnosticCodes.UpdateChecksumFailed, error.Code);
        Assert.False(Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_update_staging")));
    }

    [Fact]
    public async Task Download_RefusesAUrlThatNamesAnotherFile()
    {
        UpdateInfo update = PlatformUpdate();
        update.DownloadUrl = Download + "RPGModder-2.1.0-other.zip";
        using var service = new UpdateService(new FixedResponses(new Dictionary<string, byte[]>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync(update, new Progress<double>()));
    }

    [Fact]
    public async Task Download_RefusesChecksumsFromAnotherHost()
    {
        UpdateInfo update = PlatformUpdate();
        update.ChecksumsUrl = "https://example.com/SHA256SUMS";
        using var service = new UpdateService(new FixedResponses(new Dictionary<string, byte[]>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallAsync(update, new Progress<double>()));
    }

    [Fact]
    public void Zip_WithATopFolder_IsExtracted()
    {
        string archive = WriteZip(("RPGModder-2.1.0-win-x64/", null, 0), ("RPGModder-2.1.0-win-x64/RPGModder.exe", "binary", 0));
        string destination = _temp.Combine("out");

        UpdateService.ExtractPackage(archive, destination);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(destination, "RPGModder-2.1.0-win-x64", "RPGModder.exe")));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("folder/../../escaped.txt")]
    [InlineData("/escaped.txt")]
    public void Zip_WithAPathOutsideTheDestination_IsRefused(string entryName)
    {
        string archive = WriteZip((entryName, "unsafe", 0));
        string destination = _temp.Combine("out");

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractPackage(archive, destination));
        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
    }

    [Fact]
    public void Zip_WithASymbolicLink_IsRefused()
    {
        const int unixSymbolicLink = unchecked((int)0xA1FF0000);
        string archive = WriteZip(("RPGModder", "/etc/passwd", unixSymbolicLink));

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractPackage(archive, _temp.Combine("out")));
        Assert.False(File.Exists(_temp.Combine("out", "RPGModder")));
    }

    private static JObject Release(string version, params string[] assets)
    {
        return new JObject
        {
            ["tag_name"] = "v" + version,
            ["body"] = "notes",
            ["assets"] = new JArray(assets.Select(name => new JObject
            {
                ["name"] = name,
                ["browser_download_url"] = Download + name
            }))
        };
    }

    private static UpdateInfo PlatformUpdate()
    {
        string name = ReleaseAssets.PackagesFor(OperatingSystem.IsLinux(), Version)[0];
        return new UpdateInfo
        {
            Version = Version,
            AssetName = name,
            DownloadUrl = Download + name,
            ChecksumsUrl = Download + ReleaseAssets.Checksums
        };
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private string WriteZip(params (string Name, string? Content, int ExternalAttributes)[] entries)
    {
        string path = _temp.Combine($"package-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string? content, int attributes) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            entry.ExternalAttributes = attributes;
            if (content != null)
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return path;
    }

    /// <summary>Answers each known URL with fixed bytes and every other URL with 404.</summary>
    private sealed class FixedResponses(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = responses.TryGetValue(request.RequestUri!.ToString(), out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
