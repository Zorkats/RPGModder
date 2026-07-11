using Microsoft.Win32;
using System.Diagnostics;

namespace RPGModder.Core.Services;

// Handles nxm:// protocol registration and parsing for one-click mod installs.
// Protocol format: nxm://gameDomain/mods/modId/files/fileId?key=xxx&expires=xxx&user_id=xxx
public class NxmProtocolHandler
{
    private const string ProtocolName = "nxm";
    private const string AppName = "RPGModder";

    // Registers RPGModder as the handler for nxm:// links (requires admin on first run)
    public static bool RegisterProtocol(string exePath)
    {
        if (OperatingSystem.IsLinux())
            return RegisterLinuxProtocol(exePath);
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            // Create the protocol key
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}");
            if (key == null) return false;

            key.SetValue("", $"URL:{AppName} Protocol");
            key.SetValue("URL Protocol", "");

            // Set the icon
            using var iconKey = key.CreateSubKey("DefaultIcon");
            iconKey?.SetValue("", $"\"{exePath}\",0");

            // Set the command to execute
            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Checks if RPGModder is registered as the nxm:// handler
    public static bool IsProtocolRegistered()
    {
        if (OperatingSystem.IsLinux())
        {
            string? xdgMime = PlatformService.FindExecutable("xdg-mime");
            if (xdgMime == null)
                return false;

            string? handler = PlatformService.Capture(xdgMime,
                ["query", "default", "x-scheme-handler/nxm"]);
            if (handler?.Equals(GetLinuxDesktopFileName(), StringComparison.Ordinal) == true)
                return File.Exists(GetLinuxDesktopPath());
            if (handler?.Equals(GetPackagedLinuxDesktopFileName(), StringComparison.Ordinal) == true)
                return File.Exists(Path.Combine(GetLinuxApplicationsDirectory(), GetPackagedLinuxDesktopFileName()));
            return false;
        }
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProtocolName}\shell\open\command");
            if (key == null) return false;

            var value = key.GetValue("") as string;
            return value?.Contains("RPGModder", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch
        {
            return false;
        }
    }

    // Unregisters the nxm:// protocol handler
    public static bool UnregisterProtocol()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string desktopPath = GetLinuxDesktopPath();
                if (File.Exists(desktopPath))
                    File.Delete(desktopPath);
                RemoveLinuxMimeAssociation();
                RefreshLinuxDesktopDatabase();
                return true;
            }
            catch { return false; }
        }
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProtocolName}", false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Parses an nxm:// URL into its components
    public static NxmLink? ParseNxmUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Remove protocol prefix
        if (!url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            // Canonical URI format: nxm://gameDomain/mods/modId/files/fileId
            string gameDomain = uri.Host;
            int offset = 0;
            if (string.IsNullOrWhiteSpace(gameDomain) && segments.Length >= 5)
            {
                gameDomain = segments[0];
                offset = 1;
            }

            if (string.IsNullOrWhiteSpace(gameDomain) || segments.Length < offset + 4)
                return null;

            var link = new NxmLink
            {
                GameDomain = gameDomain,
                OriginalUrl = url
            };

            // Parse mod ID
            if (segments[offset].Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(segments[offset + 1], out int modId))
            {
                link.ModId = modId;
            }

            // Parse file ID
            if (segments[offset + 2].Equals("files", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(segments[offset + 3], out int fileId))
            {
                link.FileId = fileId;
            }

            // Parse query parameters manually
            var queryParams = ParseQueryString(uri.Query);
            
            if (queryParams.TryGetValue("key", out var key))
                link.Key = key;
            
            if (queryParams.TryGetValue("expires", out var expiresStr) && long.TryParse(expiresStr, out long expires))
                link.Expires = expires;
            
            if (queryParams.TryGetValue("user_id", out var userIdStr) && int.TryParse(userIdStr, out int userId))
                link.UserId = userId;

            return link;
        }
        catch
        {
            return null;
        }
    }

    // Simple query string parser (replaces HttpUtility.ParseQueryString)
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        if (string.IsNullOrEmpty(query)) return result;
        
        // Remove leading '?'
        if (query.StartsWith("?"))
            query = query.Substring(1);
        
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
            }
            else if (parts.Length == 1)
            {
                result[Uri.UnescapeDataString(parts[0])] = "";
            }
        }
        
        return result;
    }

    // Checks if the application was launched with an nxm:// argument
    public static NxmLink? GetLaunchLink(string[] args)
    {
        if (args.Length == 0) return null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                return ParseNxmUrl(arg);
            }
        }

        return null;
    }

    private static bool RegisterLinuxProtocol(string exePath)
    {
        try
        {
            string? xdgMime = PlatformService.FindExecutable("xdg-mime");
            if (xdgMime == null || !File.Exists(exePath))
                return false;

            string desktopPath = GetLinuxDesktopPath();
            Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
            Directory.CreateDirectory(GetLinuxConfigHome());
            string escapedExecutable = EscapeDesktopExecArgument(Path.GetFullPath(exePath));
            string desktopEntry = $"""
                [Desktop Entry]
                Type=Application
                Name=RPGModder
                Comment=Manage RPG Maker mods
                Exec="{escapedExecutable}" %u
                Terminal=false
                Categories=Game;
                MimeType=x-scheme-handler/nxm;
                NoDisplay=true
                """;
            FileTreeService.WriteAllTextAtomic(desktopPath, desktopEntry + Environment.NewLine);
            RefreshLinuxDesktopDatabase();

            return PlatformService.Run(xdgMime,
                ["default", GetLinuxDesktopFileName(), "x-scheme-handler/nxm"]);
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshLinuxDesktopDatabase()
    {
        string? updater = PlatformService.FindExecutable("update-desktop-database");
        if (updater != null)
            PlatformService.Run(updater, [Path.GetDirectoryName(GetLinuxDesktopPath())!], requireSuccess: false);
    }

    private static string GetLinuxDesktopFileName() => "rpgmodder-nxm.desktop";

    private static string GetPackagedLinuxDesktopFileName() => "rpgmodder.desktop";

    private static string GetLinuxDesktopPath()
    {
        return Path.Combine(GetLinuxApplicationsDirectory(), GetLinuxDesktopFileName());
    }

    private static string GetLinuxApplicationsDirectory()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "applications");
    }

    private static string GetLinuxConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    private static void RemoveLinuxMimeAssociation()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string[] files =
        [
            Path.Combine(GetLinuxConfigHome(), "mimeapps.list"),
            Path.Combine(dataHome, "applications", "mimeapps.list")
        ];

        foreach (string file in files.Distinct(PlatformService.PathComparer))
        {
            if (!File.Exists(file))
                continue;

            string[] lines = File.ReadAllLines(file);
            bool changed = false;
            var updated = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                const string key = "x-scheme-handler/nxm=";
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    updated.Add(line);
                    continue;
                }

                string[] handlers = line[key.Length..]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string[] remaining = handlers.Where(handler =>
                    !handler.Equals(GetLinuxDesktopFileName(), StringComparison.OrdinalIgnoreCase) &&
                    !handler.Equals(GetPackagedLinuxDesktopFileName(), StringComparison.OrdinalIgnoreCase)).ToArray();
                changed |= remaining.Length != handlers.Length;
                if (remaining.Length > 0)
                    updated.Add(key + string.Join(';', remaining) + ";");
            }

            if (changed)
                FileTreeService.WriteAllTextAtomic(file, string.Join(Environment.NewLine, updated) + Environment.NewLine);
        }
    }

    private static string EscapeDesktopExecArgument(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("`", "\\`", StringComparison.Ordinal)
             .Replace("$", "\\$", StringComparison.Ordinal);
}

// Parsed nxm:// link data
public class NxmLink
{
    public string GameDomain { get; set; } = "";
    public int ModId { get; set; }
    public int FileId { get; set; }
    public string? Key { get; set; }
    public long Expires { get; set; }
    public int UserId { get; set; }
    public string OriginalUrl { get; set; } = "";

    public bool IsValid => !string.IsNullOrEmpty(GameDomain) && ModId > 0 && FileId > 0;
    
    public bool HasDownloadParams => !string.IsNullOrEmpty(Key) && Expires > 0 && UserId > 0;

    public override string ToString() => $"nxm://{GameDomain}/mods/{ModId}/files/{FileId}";
}
