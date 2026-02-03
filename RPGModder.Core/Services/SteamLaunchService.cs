using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RPGModder.Core.Services;

/// <summary>
/// Service for detecting Steam installation and launching games through Steam
/// </summary>
public class SteamLaunchService
{
    // Known Steam AppIDs for RPG Maker games
    private static readonly Dictionary<string, int> KnownSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fear & Hunger series
        { "Fear & Hunger", 1002300 },
        { "Fear and Hunger", 1002300 },
        { "FearHunger", 1002300 },
        { "Fear & Hunger 2: Termina", 1435120 },
        { "Fear and Hunger 2: Termina", 1435120 },
        { "Fear and Hunger 2 Termina", 1435120 },
        { "Termina", 1435120 },
        
        // Other RPG Maker games
        { "Omori", 1150690 },
        { "OneShot", 420530 },
        { "To the Moon", 206440 },
        { "Lisa: The Painful", 335670 },
        { "LISA", 335670 },
        { "Ib", 1901370 },
        { "Corpse Party", 251270 },
        { "Mad Father", 483980 },
        { "The Witch's House MV", 885810 },
        { "Yume Nikki", 650700 },
        { "Undertale", 391540 },
        { "Deltarune", 1671210 },
        { "Jimmy and the Pulsating Mass", 837770 },
        { "HEARTBEAT", 984560 },
        { "Look Outside", 2714540 },
    };

    /// <summary>
    /// Get the Steam installation path from registry
    /// </summary>
    public string? GetSteamPath()
    {
        try
        {
            // Try 64-bit registry first
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
            if (key != null)
            {
                var path = key.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }

            // Try 32-bit registry
            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key32 != null)
            {
                var path = key32.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
        }
        catch
        {
            // Registry access may fail on non-Windows or restricted systems
        }

        return null;
    }

    /// <summary>
    /// Check if Steam is installed
    /// </summary>
    public bool IsSteamInstalled()
    {
        var steamPath = GetSteamPath();
        return !string.IsNullOrEmpty(steamPath) && File.Exists(Path.Combine(steamPath, "steam.exe"));
    }

    /// <summary>
    /// Detect Steam AppID for a game from various sources
    /// </summary>
    public int? DetectSteamAppId(string gameRoot, string? gameName = null)
    {
        // Method 1: Check for steam_appid.txt in game folder
        var appIdFile = Path.Combine(gameRoot, "steam_appid.txt");
        if (File.Exists(appIdFile))
        {
            var content = File.ReadAllText(appIdFile).Trim();
            if (int.TryParse(content, out int appId))
                return appId;
        }

        // Method 2: Check for steam_api.dll or steam_api64.dll and look for AppID in parent folders
        // Steam games typically have a folder structure like: steamapps/common/GameName
        var dir = new DirectoryInfo(gameRoot);
        while (dir != null)
        {
            // Check if we're in a Steam library folder
            if (dir.Name.Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                var steamAppsDir = dir.Parent;
                if (steamAppsDir != null)
                {
                    // Look for appmanifest files
                    var manifests = Directory.GetFiles(steamAppsDir.FullName, "appmanifest_*.acf");
                    foreach (var manifest in manifests)
                    {
                        var appIdFromManifest = ParseAppManifest(manifest, gameRoot);
                        if (appIdFromManifest.HasValue)
                            return appIdFromManifest;
                    }
                }
            }
            dir = dir.Parent;
        }

        // Method 3: Try to match by game name
        if (!string.IsNullOrEmpty(gameName))
        {
            if (KnownSteamAppIds.TryGetValue(gameName, out int knownAppId))
                return knownAppId;

            // Fuzzy match
            foreach (var kvp in KnownSteamAppIds)
            {
                if (gameName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Contains(gameName, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
        }

        // Method 4: Try to match folder name
        var folderName = Path.GetFileName(gameRoot);
        if (!string.IsNullOrEmpty(folderName) && KnownSteamAppIds.TryGetValue(folderName, out int folderAppId))
            return folderAppId;

        return null;
    }

    /// <summary>
    /// Parse a Steam appmanifest file to extract AppID for a specific game folder
    /// </summary>
    private int? ParseAppManifest(string manifestPath, string gameRoot)
    {
        try
        {
            var content = File.ReadAllText(manifestPath);
            
            // Extract installdir
            var installDirMatch = Regex.Match(content, @"""installdir""\s+""([^""]+)""");
            if (installDirMatch.Success)
            {
                var installDir = installDirMatch.Groups[1].Value;
                var manifestDir = Path.GetDirectoryName(manifestPath);
                if (manifestDir != null)
                {
                    var gamePath = Path.Combine(manifestDir, "common", installDir);
                    
                    // Check if this manifest is for our game
                    if (Path.GetFullPath(gamePath).Equals(Path.GetFullPath(gameRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract appid
                        var appIdMatch = Regex.Match(content, @"""appid""\s+""(\d+)""");
                        if (appIdMatch.Success && int.TryParse(appIdMatch.Groups[1].Value, out int appId))
                            return appId;
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    /// <summary>
    /// Launch a game through Steam using the steam:// protocol
    /// </summary>
    public bool LaunchThroughSteam(int appId)
    {
        try
        {
            var url = $"steam://rungameid/{appId}";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launch a game - tries Steam first if available, otherwise direct launch
    /// </summary>
    public bool LaunchGame(string exePath, string gameRoot, string? gameName = null, bool preferSteam = true)
    {
        if (preferSteam && IsSteamInstalled())
        {
            var appId = DetectSteamAppId(gameRoot, gameName);
            if (appId.HasValue)
            {
                if (LaunchThroughSteam(appId.Value))
                    return true;
            }
        }

        // Fallback to direct launch
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get a friendly name for the launch method
    /// </summary>
    public string GetLaunchMethodName(string gameRoot, string? gameName = null)
    {
        if (IsSteamInstalled())
        {
            var appId = DetectSteamAppId(gameRoot, gameName);
            if (appId.HasValue)
                return $"Steam (AppID: {appId})";
        }
        return "Direct";
    }
}
