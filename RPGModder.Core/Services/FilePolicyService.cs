using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RPGModder.Core.Services;

public enum FileAction
{
    Ignore,         // Do not pack, do not process
    ForceInclude,   // Semantic file, skip byte-matching and force pack
    Evaluate        // Standard file, byte-match to see if modified
}

public static class FilePolicyService
{
    // The master list of directories RPGModder actively manages and protects
    public static readonly string[] ProtectedFolders = new[]
    {
        "data", "js", "img", "audio", "fonts", "css", "icon", "movies", "effects", "mods", "_moddata"
    };

    public static readonly string[] ProtectedRootFiles = new[]
    {
        "index.html", "package.json"
    };

    // System binaries that should never be packed into a mod
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib"
    };

    // Evaluates a file path and determines how the Auto-Packer should handle it
    public static FileAction GetPackerAction(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath).ToLowerInvariant();

        // 1. SEMANTIC OVERRIDES (Force Include)
        if (IsConfigSave(fileName))
            return FileAction.ForceInclude;

        // 2. EXPLICIT BLACKLIST (Saves & Global Data)
        if (fileName.StartsWith("file") && (fileName.EndsWith(".rpgsave") || fileName.EndsWith(".save") || fileName.EndsWith(".rmmzsave")))
            return FileAction.Ignore;
        if (fileName is "global.rpgsave" or "global.save" or "global.rmmzsave")
            return FileAction.Ignore;

        // 3. DIRECTORY BLACKLIST
        string[] pathSegments = relativePath.Replace('\\', '/').ToLowerInvariant().Split('/');
        if (pathSegments.Any(segment => segment is ".git" or "modmanager_backups" or "save" or "saves"))
            return FileAction.Ignore;

        // 4. EXTENSION & FILENAME BLACKLIST
        string ext = Path.GetExtension(fileName);
        if (IgnoredExtensions.Contains(ext) || fileName is ".ds_store" or "thumbs.db" or "profile.json")
            return FileAction.Ignore;

        return FileAction.Evaluate;
    }

    // Centralized check for configuration save files across RPG Maker versions
    public static bool IsConfigSave(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        return lower is "config.rpgsave" or "config.save" or "config.rmmzsave";
    }
}