using Microsoft.Win32;

namespace RPGModder.Core.Services;

// Detects RPG Maker MV/MZ games installed on the system
public class GameDetectorService
{
    // Common locations to scan
    private static readonly string[] CommonPaths = new[]
    {
        @"C:\Program Files (x86)\Steam\steamapps\common",
        @"C:\Program Files\Steam\steamapps\common",
        @"D:\SteamLibrary\steamapps\common",
        @"E:\SteamLibrary\steamapps\common",
        @"F:\SteamLibrary\steamapps\common",
        @"G:\SteamLibrary\steamapps\common",
        @"C:\Games",
        @"D:\Games"
    };

    public event Action<DetectedGame>? GameFound;
    public event Action? ScanComplete;

    private readonly List<DetectedGame> _detectedGames = new();
    private bool _isScanning = false;

    public IReadOnlyList<DetectedGame> DetectedGames => _detectedGames.AsReadOnly();
    public bool IsScanning => _isScanning;

    // Starts async scan for RPG Maker games
    public async Task ScanForGamesAsync(CancellationToken cancellationToken = default)
    {
        if (_isScanning) return;
        _isScanning = true;
        _detectedGames.Clear();

        try
        {
            var searchPaths = GetSearchPaths();

            await Task.Run(() =>
            {
                foreach (var basePath in searchPaths)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!Directory.Exists(basePath)) continue;

                    try
                    {
                        ScanDirectory(basePath, 0, 3, cancellationToken);
                    }
                    catch { }
                }
            }, cancellationToken);
        }
        catch { }
        finally
        {
            _isScanning = false;
            ScanComplete?.Invoke();
        }
    }

    private void ScanDirectory(string path, int depth, int maxDepth, CancellationToken ct)
    {
        if (depth > maxDepth || ct.IsCancellationRequested) return;

        try
        {
            // Check if this directory is an RPG Maker game
            var game = TryDetectGame(path);
            if (game != null)
            {
                lock (_detectedGames)
                {
                    if (!_detectedGames.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        _detectedGames.Add(game);
                        GameFound?.Invoke(game);
                    }
                }
                return; // Don't scan subdirectories of a detected game
            }

            // Scan subdirectories
            foreach (var subDir in Directory.GetDirectories(path))
            {
                if (ct.IsCancellationRequested) break;
                
                string dirName = Path.GetFileName(subDir);
                // Skip common non-game directories
                if (dirName.StartsWith(".") || 
                    dirName.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    continue;

                ScanDirectory(subDir, depth + 1, maxDepth, ct);
            }
        }
        catch { }
    }

    private DetectedGame? TryDetectGame(string folderPath)
    {
        try
        {
            // Look for Game.exe or similar executables
            string[] possibleExeNames = { "Game.exe", "nw.exe" };
            string? exePath = null;

            foreach (var exeName in possibleExeNames)
            {
                string possiblePath = Path.Combine(folderPath, exeName);
                if (File.Exists(possiblePath))
                {
                    exePath = possiblePath;
                    break;
                }
            }

            if (exePath == null) return null;

            // Check for RPG Maker MV/MZ signature
            string jsFolder = Path.Combine(folderPath, "js");
            string wwwJsFolder = Path.Combine(folderPath, "www", "js");

            bool isMV = File.Exists(Path.Combine(jsFolder, "rpg_core.js")) ||
                        File.Exists(Path.Combine(wwwJsFolder, "rpg_core.js"));
            bool isMZ = File.Exists(Path.Combine(jsFolder, "rmmz_core.js")) ||
                        File.Exists(Path.Combine(wwwJsFolder, "rmmz_core.js"));

            if (!isMV && !isMZ) return null;

            // Get game name from folder or package.json
            string gameName = GetGameName(folderPath);
            string? iconPath = GetGameIcon(folderPath, exePath);

            return new DetectedGame
            {
                Name = gameName,
                ExePath = exePath,
                FolderPath = folderPath,
                Engine = isMZ ? RpgMakerEngine.MZ : RpgMakerEngine.MV,
                IconPath = iconPath
            };
        }
        catch
        {
            return null;
        }
    }

    private string GetGameName(string folderPath)
    {
        // Try to get name from package.json
        string packageJson = Path.Combine(folderPath, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                string json = File.ReadAllText(packageJson);
                // Simple parse for "name" field
                var match = System.Text.RegularExpressions.Regex.Match(json, @"""name""\s*:\s*""([^""]+)""");
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                    return match.Groups[1].Value;
            }
            catch { }
        }

        // Fall back to folder name
        return new DirectoryInfo(folderPath).Name;
    }

    private string? GetGameIcon(string folderPath, string exePath)
    {
        // Check for icon.png in common locations
        string[] iconPaths = {
            Path.Combine(folderPath, "icon", "icon.png"),
            Path.Combine(folderPath, "www", "icon", "icon.png"),
            Path.Combine(folderPath, "icon.png")
        };

        foreach (var path in iconPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return exePath; // Return exe path, UI can extract icon from it
    }

    private List<string> GetSearchPaths()
    {
        var paths = new List<string>(CommonPaths);

        // Try to find Steam library folders from registry/config
        try
        {
            var steamPaths = GetSteamLibraryPaths();
            paths.InsertRange(0, steamPaths);
        }
        catch { }

        return paths.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    private List<string> GetSteamLibraryPaths()
    {
        var libraryPaths = new List<string>();

        try
        {
            // Only try registry on Windows
            if (!OperatingSystem.IsWindows())
                return libraryPaths;

            // Try to read Steam install path from registry (Windows)
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                libraryPaths.Add(Path.Combine(steamPath, "steamapps", "common"));

                // Parse libraryfolders.vdf for additional libraries
                string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    string vdf = File.ReadAllText(vdfPath);
                    var matches = System.Text.RegularExpressions.Regex.Matches(vdf, @"""path""\s+""([^""]+)""");
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string libPath = match.Groups[1].Value.Replace(@"\\", @"\");
                        libraryPaths.Add(Path.Combine(libPath, "steamapps", "common"));
                    }
                }
            }
        }
        catch { }

        return libraryPaths;
    }
}

public class DetectedGame
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public RpgMakerEngine Engine { get; set; }
    public string? IconPath { get; set; }
    
    // For ComboBox display
    public override string ToString() => $"[{Engine}] {Name}";
}

public enum RpgMakerEngine
{
    MV,
    MZ
}
