using System.Text.Json;

namespace RPGModder.Core.Services;

public class GameDetectorService
{
    private readonly IReadOnlyList<string> _additionalSearchPaths;
    private readonly List<DetectedGame> _detectedGames = new();
    private bool _isScanning;

    public GameDetectorService(IEnumerable<string>? additionalSearchPaths = null)
    {
        _additionalSearchPaths = additionalSearchPaths?.ToList() ?? [];
    }

    public event Action<DetectedGame>? GameFound;
    public event Action? ScanComplete;

    public IReadOnlyList<DetectedGame> DetectedGames => _detectedGames.AsReadOnly();
    public bool IsScanning => _isScanning;

    public async Task ScanForGamesAsync(CancellationToken cancellationToken = default)
    {
        if (_isScanning)
            return;

        _isScanning = true;
        _detectedGames.Clear();

        try
        {
            IReadOnlyList<string> searchPaths = GetSearchPaths();
            await Task.Run(() =>
            {
                foreach (string basePath in searchPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(basePath))
                        continue;

                    ScanDirectory(basePath, 0, 3, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isScanning = false;
            ScanComplete?.Invoke();
        }
    }

    public DetectedGame? DetectGameAt(string folderPath)
    {
        try
        {
            string jsFolder = Path.Combine(folderPath, "js");
            string wwwJsFolder = Path.Combine(folderPath, "www", "js");
            bool isMv = File.Exists(Path.Combine(jsFolder, "rpg_core.js")) ||
                        File.Exists(Path.Combine(wwwJsFolder, "rpg_core.js"));
            bool isMz = File.Exists(Path.Combine(jsFolder, "rmmz_core.js")) ||
                        File.Exists(Path.Combine(wwwJsFolder, "rmmz_core.js"));

            if (!isMv && !isMz)
                return null;

            string? executablePath = FindGameExecutable(folderPath);
            if (executablePath == null)
                return null;

            return new DetectedGame
            {
                Name = GetGameName(folderPath),
                ExePath = executablePath,
                FolderPath = folderPath,
                Engine = isMz ? RpgMakerEngine.MZ : RpgMakerEngine.MV,
                IconPath = GetGameIcon(folderPath, executablePath)
            };
        }
        catch
        {
            return null;
        }
    }

    private void ScanDirectory(string path, int depth, int maxDepth, CancellationToken cancellationToken)
    {
        if (depth > maxDepth || cancellationToken.IsCancellationRequested)
            return;

        try
        {
            DetectedGame? game = DetectGameAt(path);
            if (game != null)
            {
                lock (_detectedGames)
                {
                    if (!_detectedGames.Any(existing =>
                        existing.ExePath.Equals(game.ExePath, PlatformService.PathComparison)))
                    {
                        _detectedGames.Add(game);
                        GameFound?.Invoke(game);
                    }
                }
                return;
            }

            foreach (string subDirectory in Directory.GetDirectories(path))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                string name = Path.GetFileName(subDirectory);
                if (name.StartsWith('.') ||
                    name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;

                ScanDirectory(subDirectory, depth + 1, maxDepth, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static string? FindGameExecutable(string folderPath)
    {
        string[] preferredNames = OperatingSystem.IsWindows()
            ? ["Game.exe", "nw.exe"]
            : ["Game", "nw", "game", "Game.exe", "nw.exe"];

        foreach (string name in preferredNames)
        {
            string candidate = Path.Combine(folderPath, name);
            if (File.Exists(candidate) && (OperatingSystem.IsWindows() ||
                candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsUnixExecutable(candidate)))
                return candidate;
        }

        if (!OperatingSystem.IsLinux())
            return null;

        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
        return Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetFileName(path), folderName, StringComparison.OrdinalIgnoreCase) ||
                           !Path.HasExtension(path))
            .FirstOrDefault(IsUnixExecutable);
    }

    private static bool IsUnixExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }

    private static string GetGameName(string folderPath)
    {
        string packageJson = Path.Combine(folderPath, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (document.RootElement.TryGetProperty("name", out JsonElement name) &&
                    !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }

        return new DirectoryInfo(folderPath).Name;
    }

    private static string? GetGameIcon(string folderPath, string executablePath)
    {
        string[] iconPaths =
        [
            Path.Combine(folderPath, "icon", "icon.png"),
            Path.Combine(folderPath, "www", "icon", "icon.png"),
            Path.Combine(folderPath, "icon.png")
        ];

        string? icon = iconPaths.FirstOrDefault(File.Exists);
        return icon ?? (OperatingSystem.IsWindows() ? executablePath : null);
    }

    private IReadOnlyList<string> GetSearchPaths()
    {
        var paths = new List<string>();
        paths.AddRange(SteamLibraryLocator.GetCommonGameDirectories());
        paths.AddRange(_additionalSearchPaths);

        if (OperatingSystem.IsWindows())
        {
            paths.AddRange(
            [
                @"C:\Program Files (x86)\Steam\steamapps\common",
                @"C:\Program Files\Steam\steamapps\common",
                @"D:\SteamLibrary\steamapps\common",
                @"E:\SteamLibrary\steamapps\common",
                @"F:\SteamLibrary\steamapps\common",
                @"G:\SteamLibrary\steamapps\common",
                @"C:\Games",
                @"D:\Games"
            ]);
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            paths.Add(Path.Combine(home, "Games"));
            paths.Add(Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"));
        }

        return paths.Where(Directory.Exists).Distinct(PlatformService.PathComparer).ToList();
    }
}

public class DetectedGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public RpgMakerEngine Engine { get; set; }
    public string? IconPath { get; set; }

    public override string ToString() => $"[{Engine}] {Name}";
}

public enum RpgMakerEngine
{
    MV,
    MZ
}
