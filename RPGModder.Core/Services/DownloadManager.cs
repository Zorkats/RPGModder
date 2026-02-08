using System.Collections.Concurrent;

namespace RPGModder.Core.Services;

// Manages mod downloads with progress tracking and queue support
public class DownloadManager : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _downloadFolder;
    private readonly ConcurrentDictionary<string, DownloadItem> _downloads = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(2); // Max 2 concurrent downloads

    public event Action<DownloadItem>? DownloadStarted;
    public event Action<DownloadItem>? DownloadProgress;
    public event Action<DownloadItem>? DownloadCompleted;
    public event Action<DownloadItem>? DownloadFailed;

    public IEnumerable<DownloadItem> ActiveDownloads => _downloads.Values.Where(d => d.Status == DownloadStatus.Downloading);
    public IEnumerable<DownloadItem> QueuedDownloads => _downloads.Values.Where(d => d.Status == DownloadStatus.Queued);
    public IEnumerable<DownloadItem> CompletedDownloads => _downloads.Values.Where(d => d.Status == DownloadStatus.Completed);

    public DownloadManager(string? downloadFolder = null)
    {
        _http = new HttpClient();
        _downloadFolder = downloadFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "RPGModder"
        );

        if (!Directory.Exists(_downloadFolder))
            Directory.CreateDirectory(_downloadFolder);
    }

    // Queues a download from a Nexus download link
    public async Task<DownloadItem> QueueDownloadAsync(
        string downloadUrl, 
        string fileName, 
        NexusMod? modInfo = null,
        NexusModFile? fileInfo = null)
    {
        var item = new DownloadItem
        {
            Id = Guid.NewGuid().ToString(),
            Url = downloadUrl,
            FileName = SanitizeFileName(fileName),
            DestinationPath = Path.Combine(_downloadFolder, SanitizeFileName(fileName)),
            Status = DownloadStatus.Queued,
            ModInfo = modInfo,
            FileInfo = fileInfo,
            QueuedAt = DateTime.Now
        };

        _downloads[item.Id] = item;

        // Start download in background
        _ = ProcessDownloadAsync(item);

        return item;
    }

    private async Task ProcessDownloadAsync(DownloadItem item)
    {
        try
        {
            await _downloadSemaphore.WaitAsync(item.CancelToken);
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Cancelled;
            return;
        }

        try
        {
            item.Status = DownloadStatus.Downloading;
            item.StartedAt = DateTime.Now;
            DownloadStarted?.Invoke(item);

            using var response = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, item.CancelToken);
            response.EnsureSuccessStatusCode();

            // Resolve the real filename from Content-Disposition header or URL
            // This fixes the NXM flow where only mod name (no extension) is passed
            string resolvedFileName = ResolveFileName(response, item.FileName);
            if (resolvedFileName != item.FileName)
            {
                item.FileName = resolvedFileName;
                item.DestinationPath = Path.Combine(_downloadFolder, resolvedFileName);
            }

            item.TotalBytes = response.Content.Headers.ContentLength ?? 0;

            var tempPath = item.DestinationPath + ".downloading";

            using (var contentStream = await response.Content.ReadAsStreamAsync(item.CancelToken))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;
                var lastProgressUpdate = DateTime.Now;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, item.CancelToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, item.CancelToken);
                    totalRead += bytesRead;
                    item.DownloadedBytes = totalRead;

                    // Throttle progress updates to every 100ms
                    if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 100)
                    {
                        item.Progress = item.TotalBytes > 0 
                            ? (double)totalRead / item.TotalBytes * 100 
                            : 0;
                        DownloadProgress?.Invoke(item);
                        lastProgressUpdate = DateTime.Now;
                    }
                }
            }

            // Move temp file to final destination
            if (File.Exists(item.DestinationPath))
                File.Delete(item.DestinationPath);
            File.Move(tempPath, item.DestinationPath);

            item.Status = DownloadStatus.Completed;
            item.CompletedAt = DateTime.Now;
            item.Progress = 100;
            DownloadCompleted?.Invoke(item);
        }
        catch (OperationCanceledException)
        {
            item.Status = DownloadStatus.Cancelled;
            CleanupTempFile(item);
        }
        catch (Exception ex)
        {
            item.Status = DownloadStatus.Failed;
            item.Error = ex.Message;
            DownloadFailed?.Invoke(item);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    // Cancels a download
    public void CancelDownload(string downloadId)
    {
        if (_downloads.TryGetValue(downloadId, out var item))
        {
            item.Cts.Cancel();
            item.Status = DownloadStatus.Cancelled;
            CleanupTempFile(item);
        }
    }

    private static void CleanupTempFile(DownloadItem item)
    {
        var tempPath = item.DestinationPath + ".downloading";
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // Removes a download from the list
    public void RemoveDownload(string downloadId)
    {
        _downloads.TryRemove(downloadId, out _);
    }

    // Clears completed downloads from the list
    public void ClearCompleted()
    {
        var completed = _downloads.Values.Where(d => 
            d.Status == DownloadStatus.Completed || 
            d.Status == DownloadStatus.Failed ||
            d.Status == DownloadStatus.Cancelled
        ).ToList();

        foreach (var item in completed)
        {
            _downloads.TryRemove(item.Id, out _);
        }
    }

    // Gets the download folder path
    public string GetDownloadFolder() => _downloadFolder;

    private string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    // Extracts the real filename from HTTP response headers or URL.
    // Falls back to the original name with .zip appended if no extension is found.
    private string ResolveFileName(HttpResponseMessage response, string fallbackName)
    {
        string? resolved = null;

        // Try Content-Disposition header first (most reliable)
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (contentDisposition?.FileName != null)
        {
            resolved = contentDisposition.FileName.Trim('"', ' ');
        }
        else if (contentDisposition?.FileNameStar != null)
        {
            resolved = contentDisposition.FileNameStar.Trim('"', ' ');
        }

        // Try the URL path as second option
        if (string.IsNullOrEmpty(resolved))
        {
            try
            {
                var uri = new Uri(response.RequestMessage?.RequestUri?.ToString() ?? "");
                var urlFileName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(urlFileName) && urlFileName.Contains('.'))
                    resolved = urlFileName;
            }
            catch { }
        }

        // If we got a real name, sanitize and return it
        if (!string.IsNullOrEmpty(resolved))
            return SanitizeFileName(resolved);

        // Fallback: if the original name has no extension, append .zip
        if (!Path.HasExtension(fallbackName))
            return SanitizeFileName(fallbackName + ".zip");

        return SanitizeFileName(fallbackName);
    }

    public void Dispose()
    {
        _http.Dispose();
        _downloadSemaphore.Dispose();
    }
}

public class DownloadItem
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public DownloadStatus Status { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double Progress { get; set; }
    public string? Error { get; set; }
    
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Cancellation support
    public CancellationTokenSource Cts { get; set; } = new();
    public CancellationToken CancelToken => Cts.Token;

    // Associated mod info
    public NexusMod? ModInfo { get; set; }
    public NexusModFile? FileInfo { get; set; }

    // Convenience properties
    public string SizeFormatted => FormatBytes(TotalBytes);
    public string DownloadedFormatted => FormatBytes(DownloadedBytes);
    public string ProgressFormatted => $"{Progress:F0}%";
    public string SpeedFormatted
    {
        get
        {
            if (StartedAt == null || Status != DownloadStatus.Downloading)
                return "";
            
            var elapsed = (DateTime.Now - StartedAt.Value).TotalSeconds;
            if (elapsed <= 0) return "";
            
            var bytesPerSecond = DownloadedBytes / elapsed;
            return $"{FormatBytes((long)bytesPerSecond)}/s";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled
}
