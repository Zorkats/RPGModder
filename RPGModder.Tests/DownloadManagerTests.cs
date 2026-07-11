using RPGModder.Core.Services;

namespace RPGModder.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderDownloadTests_{Guid.NewGuid():N}");
    private readonly DownloadManager _manager;

    public DownloadManagerTests()
    {
        _manager = new DownloadManager(_root);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Theory]
    [InlineData("http://example.com/mod.zip")]
    [InlineData("file:///C:/mod.zip")]
    [InlineData("not-a-url")]
    public async Task QueueDownloadAsync_RejectsNonHttpsUrls(string url)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _manager.QueueDownloadAsync(url, "mod.zip"));
    }
}
