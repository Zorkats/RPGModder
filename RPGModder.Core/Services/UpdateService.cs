using Newtonsoft.Json.Linq;
using RPGModder.Core.Updates;
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
    private const long MaximumPackageBytes = 512L * 1024L * 1024L;
    private const long MaximumChecksumsBytes = 64L * 1024L;
    private const long MaximumExtractedBytes = 2L * 1024L * 1024L * 1024L;
    private const int MaximumEntries = 100_000;

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
        : this(new HttpClientHandler())
    {
    }

    /// <summary>For tests: the handler answers every request, so no test reaches the network.</summary>
    internal UpdateService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RPGModder", "1.0"));
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            string url = $"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest";
            string json = await _http.GetStringAsync(url);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            {
                return SelectRelease(JObject.Parse(json), CurrentVersion, OperatingSystem.IsLinux());
            }
        }
        catch (Exception ex)
        {
            // An update check that fails only means no update is offered this time.
            Trace.TraceWarning($"The update check failed: {ex.Message}");
        }

        return null;
    }

    // Leftover download files are only clutter in the temp folder, so a failure is logged and the update goes on.
    private static void TryCleanUp(Action delete, string path)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"An update download file could not be removed: {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The update a GitHub release offers on one platform, or null (CI-11, TX-13). The package is chosen by its
    /// exact <see cref="ReleaseAssets"/> name, and a release without <c>SHA256SUMS</c> offers nothing, because
    /// its package could not be verified.
    /// </summary>
    internal static UpdateInfo? SelectRelease(JObject release, string currentVersion, bool linux)
    {
        string latestTag = release["tag_name"]?.ToString().TrimStart('v') ?? "0.0.0";
        if (!IsNewer(latestTag, currentVersion))
        {
            return null;
        }

        List<JToken> assets = release["assets"]?.Children().ToList() ?? [];
        string? AssetUrl(string name)
        {
            List<JToken> matches = assets.Where(asset => string.Equals(asset["name"]?.ToString(), name, StringComparison.Ordinal)).ToList();
            string? url = matches.Count == 1 ? matches[0]["browser_download_url"]?.ToString() : null;
            return url != null && IsOfficialUrl(url) ? url : null;
        }

        string? checksumsUrl = AssetUrl(ReleaseAssets.Checksums);
        if (checksumsUrl == null)
        {
            Trace.TraceWarning($"Release {latestTag} has no {ReleaseAssets.Checksums}, so it is not offered.");
            return null;
        }

        foreach (string name in ReleaseAssets.PackagesFor(linux, latestTag))
        {
            if (AssetUrl(name) is { } downloadUrl)
            {
                return new UpdateInfo
                {
                    Version = latestTag,
                    AssetName = name,
                    DownloadUrl = downloadUrl,
                    ChecksumsUrl = checksumsUrl,
                    ReleaseNotes = release["body"]?.ToString() ?? "No release notes."
                };
            }
        }

        Trace.TraceWarning($"Release {latestTag} has no package for this platform.");
        return null;
    }

    /// <summary>
    /// Downloads the package and <c>SHA256SUMS</c>, verifies the package's SHA-256, and only then stages it (TX-13).
    /// A missing or wrong hash throws <see cref="UpdateChecksumException"/> before anything is extracted.
    /// </summary>
    public async Task DownloadAndInstallAsync(UpdateInfo update, IProgress<double> progress)
    {
        if (!IsOfficialUrl(update.DownloadUrl) || !IsOfficialUrl(update.ChecksumsUrl))
        {
            throw new InvalidOperationException("The update URL is not an official GitHub release URL.");
        }

        Uri uri = new(update.DownloadUrl);
        if (!string.Equals(Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)), update.AssetName, StringComparison.Ordinal) ||
            !ReleaseAssets.PackagesFor(OperatingSystem.IsLinux(), update.Version).Contains(update.AssetName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The update URL does not name the expected release package.");
        }

        // 1. Download the checksums, then the package, to Temp
        string sums = await DownloadTextAsync(update.ChecksumsUrl, MaximumChecksumsBytes);
        string packageExtension = update.AssetName.EndsWith(".tar.gz", StringComparison.Ordinal) ? ".tar.gz" : ".zip";
        string tempFile = Path.Combine(Path.GetTempPath(), $"RPGModder_Update_{Guid.NewGuid():N}{packageExtension}");

        using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumPackageBytes)
            {
                throw new InvalidDataException("The update package exceeds the 512 MB safety limit.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? 10 * 1024 * 1024;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(tempFile, FileMode.CreateNew);
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                totalRead += bytesRead;
                if (totalRead > MaximumPackageBytes)
                {
                    throw new InvalidDataException("The update package exceeds the 512 MB safety limit.");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                progress.Report(Math.Min(100, (double)totalRead / totalBytes * 100));
            }
        }

        try
        {
            ReleaseChecksums.Verify(tempFile, sums, update.AssetName);
        }
        catch (UpdateChecksumException)
        {
            TryCleanUp(() => File.Delete(tempFile), tempFile);
            throw;
        }

        // 2. Extract to a staging folder
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string extractFolder = Path.Combine(Path.GetTempPath(), $"RPGModder_Extract_{Guid.NewGuid():N}");
        if (Directory.Exists(extractFolder)) Directory.Delete(extractFolder, true);
        Directory.CreateDirectory(extractFolder);

        ExtractPackage(tempFile, extractFolder);

        // Find the actual content root: the archive may wrap everything in one folder
        string contentRoot = extractFolder;
        var topDirs = Directory.GetDirectories(extractFolder);
        var topFiles = Directory.GetFiles(extractFolder);
        if (topDirs.Length == 1 && topFiles.Length == 0)
        {
            // The archive wraps everything in one folder, so that folder is the root
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

        TryCleanUp(() => File.Delete(tempFile), tempFile);
        TryCleanUp(() => Directory.Delete(extractFolder, true), extractFolder);

        if (OperatingSystem.IsLinux())
        {
            PrepareLinuxUpdate(currentDir, updateStaging, expectedExecutable);
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Automatic updates are supported on Windows and Linux.");

        PrepareWindowsUpdate(currentDir, updateStaging);
    }

    private async Task<string> DownloadTextAsync(string url, long maximumBytes)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new UpdateChecksumException($"{ReleaseAssets.Checksums} is larger than expected, so the update was not applied.");
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            if (limited.Length + bytesRead > maximumBytes)
            {
                throw new UpdateChecksumException($"{ReleaseAssets.Checksums} is larger than expected, so the update was not applied.");
            }

            limited.Write(buffer, 0, bytesRead);
        }

        return System.Text.Encoding.UTF8.GetString(limited.ToArray());
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
            "%SystemRoot%\System32\timeout.exe" /t 2 /nobreak >nul

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
            ExtractZipSafely(archivePath, destinationDirectory);
            return;
        }

        throw new InvalidDataException("The update package format is not supported.");
    }

    /// <summary>
    /// Extracts a zip with the tar path's limits (TX-13): at most 100,000 entries and 2 GB, every path inside
    /// the destination, and no symbolic link entries.
    /// </summary>
    private static void ExtractZipSafely(string archivePath, string destinationDirectory)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        const int unixPermissionMask = 0x1FF;
        Directory.CreateDirectory(destinationDirectory);
        long extractedBytes = 0;

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("The update package contains too many entries.");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            int unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & unixFileTypeMask) == unixSymbolicLink)
                throw new InvalidDataException($"The update package contains a symbolic link: {entry.FullName}.");

            string relativePath = entry.FullName.Replace('\\', '/');
            string destination = SafePathService.ResolveContainedPath(destinationDirectory, relativePath, "zip entry path");
            if (relativePath.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedBytes)
                throw new InvalidDataException("The extracted update package exceeds the 2 GB safety limit.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (Stream input = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyAtMost(input, output, entry.Length);
            }

            if (OperatingSystem.IsLinux() && (unixMode & unixPermissionMask) != 0)
                File.SetUnixFileMode(destination, (UnixFileMode)(unixMode & unixPermissionMask));
        }
    }

    /// <summary>Copies a zip entry, refusing more bytes than its header declares.</summary>
    private static void CopyAtMost(Stream input, Stream output, long declaredLength)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            copied += read;
            if (copied > declaredLength)
                throw new InvalidDataException("A zip entry in the update package is larger than its header declares.");
            output.Write(buffer, 0, read);
        }
    }

    private static void ExtractTarSafely(Stream archive, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        long extractedBytes = 0;
        int entryCount = 0;

        using var reader = new TarReader(archive, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (++entryCount > MaximumEntries)
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
                    if (extractedBytes > MaximumExtractedBytes)
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

    private static bool IsNewer(string latest, string current)
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

    private static bool IsOfficialUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               IsOfficialGitHubHost(uri.Host);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ChecksumsUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}
