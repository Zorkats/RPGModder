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
            // Gets the version from the .exe itself (set in .csproj)
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        }
    }

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient();
        // GitHub API requires a User-Agent
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RPGModder", "1.0"));
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest";
            string json = await _http.GetStringAsync(url);
            var release = JObject.Parse(json);

            // Compare tags
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
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(tempFile, FileMode.Create))
            {
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
        }

        // 2. Extract specific EXE (or all files) to current folder as "RPGModder_New.exe"
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string newExePath = Path.Combine(currentDir, "RPGModder_New.exe");

        // Extract ZIP to a temp folder first
        string extractFolder = Path.Combine(Path.GetTempPath(), "RPGModder_Extract");
        if (Directory.Exists(extractFolder)) Directory.Delete(extractFolder, true);
        Directory.CreateDirectory(extractFolder);

        ZipFile.ExtractToDirectory(tempFile, extractFolder);

        // Move the main executable (and other files if needed)
        string downloadedExe = Directory.GetFiles(extractFolder, "*.exe").FirstOrDefault() ?? "";
        if (File.Exists(downloadedExe))
        {
            File.Copy(downloadedExe, newExePath, true);
        }

        // 3. Create the "Self-Destruct" Batch Script
        string batPath = Path.Combine(currentDir, "update_restart.bat");
        string exeName = Process.GetCurrentProcess().MainModule?.FileName ?? "RPGModder.exe";
        string exeFileName = Path.GetFileName(exeName);

        string script = $@"
@echo off
timeout /t 2 /nobreak
del ""{exeFileName}""
move ""RPGModder_New.exe"" ""{exeFileName}""
start """" ""{exeFileName}""
del ""%~f0""
";
        File.WriteAllText(batPath, script);

        // 4. Launch Script and Kill Self
        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            CreateNoWindow = true
        });

        Environment.Exit(0);
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