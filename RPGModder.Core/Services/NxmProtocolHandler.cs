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
        if (!OperatingSystem.IsWindows())
            return false;

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
        if (!OperatingSystem.IsWindows())
            return false;

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
        if (!OperatingSystem.IsWindows())
            return false;

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

            // Expected format: gameDomain/mods/modId/files/fileId
            if (segments.Length < 5)
                return null;

            var link = new NxmLink
            {
                GameDomain = segments[0],
                OriginalUrl = url
            };

            // Parse mod ID
            if (segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase) && 
                int.TryParse(segments[2], out int modId))
            {
                link.ModId = modId;
            }

            // Parse file ID
            if (segments[3].Equals("files", StringComparison.OrdinalIgnoreCase) && 
                int.TryParse(segments[4], out int fileId))
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
