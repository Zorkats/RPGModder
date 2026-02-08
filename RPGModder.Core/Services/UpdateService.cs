using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;

namespace RPGModder.Core.Services;

public class UpdateService
{
    private const string REPO_OWNER = "Zorkats";
    private const string REPO_NAME = "RPGModder";

    public string CurrentVersion
    {
        get
        {
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        }
    }

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RPGModder", "1.0"));
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest";
            string json = await _http.GetStringAsync(url);
            var release = JObject.Parse(json);

            string latestTag = release["tag_name"]?.ToString().TrimStart('v') ?? "0.0.0";

            if (IsNewer(latestTag, CurrentVersion))
            {
                return new UpdateInfo
                {
                    Version = latestTag,
                    DownloadUrl = release["assets"]?[0]?["browser_download_url"]?.ToString() ?? "",
                    ReleaseNotes = release["body"]?.ToString() ?? "No release notes."
                };
            }
        }
        catch { }
        return null;
    }

    public async Task DownloadAndInstallAsync(string url, IProgress<double> progress)
    {
        // 1. Download to Temp
        string tempFile = Path.Combine(Path.GetTempPath(), "RPGModder_Update.zip");
        if (File.Exists(tempFile)) File.Delete(tempFile);

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            var totalBytes = response.Content.Headers.ContentLength ?? 10 * 1024 * 1024;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(tempFile, FileMode.Create);
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;
                progress.Report((double)totalRead / totalBytes * 100);
            }
        }

        // 2. Extract to a staging folder
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string extractFolder = Path.Combine(Path.GetTempPath(), $"RPGModder_Extract_{Guid.NewGuid():N}");
        if (Directory.Exists(extractFolder)) Directory.Delete(extractFolder, true);
        Directory.CreateDirectory(extractFolder);

        ZipFile.ExtractToDirectory(tempFile, extractFolder);

        // Find the actual content root — ZIP may contain a single subfolder
        string contentRoot = extractFolder;
        var topDirs = Directory.GetDirectories(extractFolder);
        var topFiles = Directory.GetFiles(extractFolder);
        if (topDirs.Length == 1 && topFiles.Length == 0)
        {
            // ZIP wraps everything in one folder — use it as root
            contentRoot = topDirs[0];
        }

        // 3. Stage ALL files into an update folder next to the app
        string updateStaging = Path.Combine(currentDir, "_update_staging");
        if (Directory.Exists(updateStaging)) Directory.Delete(updateStaging, true);
        CopyDirectory(contentRoot, updateStaging);

        // 4. Create the update batch script
        //    - Waits for the current process to exit
        //    - Copies all files from staging to app directory (overwriting)
        //    - Cleans up staging folder
        //    - Relaunches the app
        string batPath = Path.Combine(currentDir, "update_restart.bat");
        string exeFileName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "RPGModder.exe");

        string script = $"""
            @echo off
            echo Waiting for RPGModder to close...
            timeout /t 2 /nobreak >nul
            
            echo Applying update...
            xcopy /s /y /q "_update_staging\*" "." >nul 2>&1
            
            echo Cleaning up...
            rmdir /s /q "_update_staging" >nul 2>&1
            
            echo Restarting...
            start "" "{exeFileName}"
            
            del "%~f0"
            """;
        File.WriteAllText(batPath, script);

        // 5. Clean up temp files we can clean now
        try { File.Delete(tempFile); } catch { }
        try { Directory.Delete(extractFolder, true); } catch { }

        // 6. Launch script and exit
        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WorkingDirectory = currentDir
        });

        Environment.Exit(0);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

    private bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var v1) && Version.TryParse(current, out var v2))
        {
            return v1 > v2;
        }
        return false;
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}
