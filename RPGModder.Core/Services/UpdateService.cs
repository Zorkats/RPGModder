using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;

namespace RPGModder.Core.Services;

public class UpdateService : IDisposable
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
                var assets = release["assets"]?.Children() ?? Enumerable.Empty<JToken>();
                string platformToken = OperatingSystem.IsLinux() ? "linux" : "win";
                JToken? package = assets.FirstOrDefault(asset =>
                    IsSupportedPackageName(asset["name"]?.ToString()) &&
                    asset["name"]?.ToString().Contains(platformToken, StringComparison.OrdinalIgnoreCase) == true);
                string downloadUrl = package?["browser_download_url"]?.ToString() ?? "";
                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !IsOfficialGitHubHost(uri.Host))
                {
                    return null;
                }

                return new UpdateInfo
                {
                    Version = latestTag,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = release["body"]?.ToString() ?? "No release notes."
                };
            }
        }
        catch { }
        return null;
    }

    public async Task DownloadAndInstallAsync(string url, IProgress<double> progress)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !IsOfficialGitHubHost(uri.Host))
        {
            throw new InvalidOperationException("The update URL is not an official GitHub release URL.");
        }

        // 1. Download to Temp
        string packageExtension = uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? ".tar.gz"
            : ".zip";
        string tempFile = Path.Combine(Path.GetTempPath(), "RPGModder_Update" + packageExtension);
        if (File.Exists(tempFile)) File.Delete(tempFile);

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512L * 1024L * 1024L)
            {
                throw new InvalidDataException("The update package exceeds the 512 MB safety limit.");
            }

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

        ExtractPackage(tempFile, extractFolder);

        // Find the actual content root — ZIP may contain a single subfolder
        string contentRoot = extractFolder;
        var topDirs = Directory.GetDirectories(extractFolder);
        var topFiles = Directory.GetFiles(extractFolder);
        if (topDirs.Length == 1 && topFiles.Length == 0)
        {
            // ZIP wraps everything in one folder — use it as root
            contentRoot = topDirs[0];
        }

        string expectedExecutable = OperatingSystem.IsWindows() ? "RPGModder.exe" : "RPGModder";
        if (!File.Exists(Path.Combine(contentRoot, expectedExecutable)))
        {
            throw new InvalidDataException($"The update archive does not contain {expectedExecutable}.");
        }

        // 3. Stage ALL files into an update folder next to the app
        string updateStaging = Path.Combine(currentDir, "_update_staging");
        if (Directory.Exists(updateStaging)) Directory.Delete(updateStaging, true);
        CopyDirectory(contentRoot, updateStaging);

        try { File.Delete(tempFile); } catch { }
        try { Directory.Delete(extractFolder, true); } catch { }

        if (OperatingSystem.IsLinux())
        {
            PrepareLinuxUpdate(currentDir, updateStaging, expectedExecutable);
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Automatic updates are supported on Windows and Linux.");

        PrepareWindowsUpdate(currentDir, updateStaging);
    }

    private static void PrepareWindowsUpdate(string currentDir, string updateStaging)
    {
        // 2.0.0 created this folder inside the install and could never fill it.
        string legacyBackup = Path.Combine(currentDir, "_update_backup");
        if (Directory.Exists(legacyBackup)) Directory.Delete(legacyBackup, true);

        string exeFileName = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "RPGModder.exe");
        string backup = Path.Combine(Path.GetTempPath(), $"RPGModder_Backup_{Guid.NewGuid():N}");
        string batPath = WriteWindowsUpdateFiles(currentDir, exeFileName, backup);

        Process.Start(new ProcessStartInfo
        {
            FileName = batPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WorkingDirectory = currentDir
        });

        Environment.Exit(0);
    }

    private static void PrepareLinuxUpdate(string currentDir, string updateStaging, string executableName)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException();

        string executable = Directory.GetFiles(updateStaging, executableName, SearchOption.AllDirectories).First();
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(executable);
            File.SetUnixFileMode(executable, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The downloaded Linux executable could not be made executable.", ex);
        }

        string backup = Path.Combine(Path.GetTempPath(), $"RPGModder_Backup_{Guid.NewGuid():N}");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"RPGModder_Update_{Guid.NewGuid():N}.sh");
        string script = BuildLinuxUpdateScript(
            Path.TrimEndingDirectorySeparator(currentDir),
            updateStaging,
            backup,
            Path.Combine(currentDir, executableName),
            scriptPath,
            waitSeconds: 2);
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { scriptPath }
        });
        Environment.Exit(0);
    }

    /// <summary>
    /// Writes the update script and its exclude list into the install folder and returns the script path.
    /// </summary>
    internal static string WriteWindowsUpdateFiles(string currentDir, string exeFileName, string backupDir)
    {
        string batPath = Path.Combine(currentDir, "update_restart.bat");
        File.WriteAllLines(Path.Combine(currentDir, "update_excludes.txt"),
        [
            "_update_staging",
            "update_restart.bat",
            "update_excludes.txt"
        ]);
        File.WriteAllText(batPath, BuildWindowsUpdateScript(exeFileName, backupDir));
        return batPath;
    }

    /// <summary>
    /// Builds the batch script that swaps in a staged update after RPGModder exits.
    /// Mirrors the Linux script: no backup means no update, and a failed restore keeps the backup.
    /// The backup folder must be outside the install, because xcopy refuses to copy a folder into itself.
    /// </summary>
    internal static string BuildWindowsUpdateScript(string exeFileName, string backupDir)
    {
        // A percent sign in a batch file starts a variable, so a literal one is doubled.
        string backup = backupDir.Replace("%", "%%", StringComparison.Ordinal);

        return $"""
            @echo off
            echo Waiting for RPGModder to close...
            timeout /t 2 /nobreak >nul

            echo Creating rollback backup...
            mkdir "{backup}" >nul 2>&1
            xcopy /s /y /q ".\*" "{backup}\" /exclude:update_excludes.txt >nul 2>&1
            if errorlevel 1 goto abort

            echo Applying update...
            xcopy /s /y /q "_update_staging\*" "." >nul 2>&1
            if errorlevel 1 goto rollback

            echo Cleaning up...
            rmdir /s /q "_update_staging" >nul 2>&1
            rmdir /s /q "{backup}" >nul 2>&1
            del "update_excludes.txt" >nul 2>&1
            echo Restarting...
            start "" "{exeFileName}"
            del "%~f0"
            exit /b 0

            :rollback
            echo Update failed. Restoring previous version...
            xcopy /s /y /q "{backup}\*" "." >nul 2>&1
            if errorlevel 1 goto keepbackup
            rmdir /s /q "_update_staging" >nul 2>&1
            rmdir /s /q "{backup}" >nul 2>&1
            del "update_excludes.txt" >nul 2>&1
            start "" "{exeFileName}"
            del "%~f0"
            exit /b 1

            :abort
            echo The current version could not be backed up, so the update was not applied.
            rmdir /s /q "_update_staging" >nul 2>&1
            rmdir /s /q "{backup}" >nul 2>&1
            del "update_excludes.txt" >nul 2>&1
            start "" "{exeFileName}"
            del "%~f0"
            exit /b 1

            :keepbackup
            echo The previous version could not be restored. A full backup is kept at: "{backup}"
            rmdir /s /q "_update_staging" >nul 2>&1
            del "update_excludes.txt" >nul 2>&1
            pause
            exit /b 1
            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// Builds the POSIX script that swaps in a staged update after RPGModder exits.
    /// The previous version is backed up first; if the update copy fails it is restored,
    /// and if the backup or the restore fails the backup is kept instead of deleted.
    /// </summary>
    internal static string BuildLinuxUpdateScript(
        string currentDir,
        string updateStaging,
        string backupDir,
        string executablePath,
        string scriptPath,
        int waitSeconds)
    {
        string current = ShellQuote(currentDir);
        string staging = ShellQuote(updateStaging);
        string backup = ShellQuote(backupDir);
        string executable = ShellQuote(executablePath);
        string self = ShellQuote(scriptPath);

        return $"""
            #!/bin/sh
            set -u
            sleep {waitSeconds}
            if ! mkdir -p {backup} || ! cp -a {current}/. {backup}/; then
                echo "RPGModder update skipped: the current version could not be backed up." >&2
                rm -rf {staging} {backup}
                rm -f {self}
                exec {executable}
            fi
            if cp -a {staging}/. {current}/; then
                chmod u+x {executable}
                rm -rf {staging} {backup}
                rm -f {self}
                exec {executable}
            fi
            echo "RPGModder update failed. Restoring the previous version..." >&2
            if cp -a {backup}/. {current}/; then
                rm -rf {staging} {backup}
                rm -f {self}
                exec {executable}
            fi
            echo "RPGModder could not restore the previous version. A full backup is kept at:" {backup} >&2
            exit 1

            """.ReplaceLineEndings("\n");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }

    internal static void ExtractPackage(string archivePath, string destinationDirectory)
    {
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = File.OpenRead(archivePath);
            using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            ExtractTarSafely(gzip, destinationDirectory);
            return;
        }

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory);
            return;
        }

        throw new InvalidDataException("The update package format is not supported.");
    }

    private static void ExtractTarSafely(Stream archive, string destinationDirectory)
    {
        const long maximumExtractedBytes = 2L * 1024L * 1024L * 1024L;
        const int maximumEntries = 100_000;
        Directory.CreateDirectory(destinationDirectory);
        long extractedBytes = 0;
        int entryCount = 0;

        using var reader = new TarReader(archive, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (++entryCount > maximumEntries)
                throw new InvalidDataException("The update package contains too many entries.");

            string relativePath = entry.Name.Replace('\\', '/');
            string destination = SafePathService.ResolveContainedPath(
                destinationDirectory, relativePath, "tar entry path");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    extractedBytes = checked(extractedBytes + entry.Length);
                    if (extractedBytes > maximumExtractedBytes)
                        throw new InvalidDataException("The extracted update package exceeds the 2 GB safety limit.");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        entry.DataStream?.CopyTo(output);
                    }
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(destination, entry.Mode);
                    break;

                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;

                default:
                    throw new InvalidDataException(
                        $"The update package contains an unsupported or unsafe tar entry: {entry.EntryType}.");
            }
        }
    }

    private bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var v1) && Version.TryParse(current, out var v2))
        {
            return v1 > v2;
        }
        return false;
    }

    private static bool IsOfficialGitHubHost(string host)
    {
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedPackageName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (OperatingSystem.IsWindows())
            return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux())
            return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}
