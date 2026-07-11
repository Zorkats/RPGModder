using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace RPGModder.Core.Services;

public static class SteamLibraryLocator
{
    public static IReadOnlyList<string> GetSteamRoots()
    {
        var roots = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            AddWindowsRegistryRoots(roots);
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddIfDirectory(roots, Path.Combine(home, ".steam", "steam"));
            AddIfDirectory(roots, Path.Combine(home, ".local", "share", "Steam"));
            AddIfDirectory(roots, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            AddIfDirectory(roots, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam"));
        }

        return roots.Distinct(PlatformService.PathComparer).ToList();
    }

    public static IReadOnlyList<string> GetLibraryRoots()
    {
        var libraries = new List<string>();
        foreach (string steamRoot in GetSteamRoots())
        {
            libraries.Add(steamRoot);
            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
                continue;

            try
            {
                string vdf = File.ReadAllText(vdfPath);
                foreach (Match match in Regex.Matches(vdf, @"""path""\s+""([^""]+)"""))
                {
                    string path = match.Groups[1].Value.Replace(@"\\", @"\");
                    AddIfDirectory(libraries, path);
                }
            }
            catch { }
        }

        return libraries.Distinct(PlatformService.PathComparer).ToList();
    }

    public static IReadOnlyList<string> GetCommonGameDirectories() =>
        GetLibraryRoots()
            .Select(root => Path.Combine(root, "steamapps", "common"))
            .Where(Directory.Exists)
            .Distinct(PlatformService.PathComparer)
            .ToList();

    private static void AddWindowsRegistryRoots(List<string> roots)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var currentUser = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (currentUser?.GetValue("SteamPath") is string userPath)
                AddIfDirectory(roots, userPath);

            using var localMachine64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam");
            if (localMachine64?.GetValue("InstallPath") is string machinePath64)
                AddIfDirectory(roots, machinePath64);

            using var localMachine32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (localMachine32?.GetValue("InstallPath") is string machinePath32)
                AddIfDirectory(roots, machinePath32);
        }
        catch { }
    }

    private static void AddIfDirectory(List<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            paths.Add(Path.GetFullPath(path));
    }
}
