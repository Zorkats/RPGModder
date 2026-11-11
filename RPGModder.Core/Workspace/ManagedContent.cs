using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Which files under a content root RPGModder manages: the protected folders and root files, minus
/// the entries of the 1.x mod library (GR-V11). The same rule applies to the live folder and to a
/// backup folder, so a library entry never enters a backup and is never restored or deleted.
/// </summary>
public static class ManagedContent
{
    public const string LegacyLibraryFolder = "mods";

    /// <summary>Path comparison as the file system compares names.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether a content-relative path lies in a protected folder or is a protected root file.</summary>
    public static bool IsManaged(string relativePath)
    {
        string[] segments = Split(relativePath);
        if (segments.Length == 1)
        {
            return FilePolicyService.ProtectedRootFiles.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
        }

        return segments.Length > 1 && FilePolicyService.ProtectedFolders.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a content-relative path lies inside a 1.x library entry under <paramref name="root"/>:
    /// a direct subfolder of <c>mods</c> that holds a <c>mod.json</c> at its top.
    /// </summary>
    public static bool IsLegacyLibraryEntry(string root, string relativePath)
    {
        string[] segments = Split(relativePath);
        return segments.Length > 2 &&
               segments[0].Equals(LegacyLibraryFolder, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(root, segments[0], segments[1], ModStore.ManifestFileName));
    }

    /// <summary>The 1.x library entries directly under <c>&lt;root&gt;/mods</c>, as content-relative folder paths.</summary>
    public static IReadOnlyList<string> LegacyLibraryEntries(string root)
    {
        string mods = Path.Combine(root, LegacyLibraryFolder);
        if (!Directory.Exists(mods))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(mods)
            .Where(folder => File.Exists(Path.Combine(folder, ModStore.ManifestFileName)))
            .Select(folder => Path.GetRelativePath(root, folder).Replace('\\', '/'))
            .OrderBy(folder => folder, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every managed file under a root, content-relative with forward slashes, ordered. Links are skipped.</summary>
    public static IReadOnlyList<string> List(string root)
    {
        var files = new List<string>();
        foreach (string file in FilePolicyService.ProtectedRootFiles)
        {
            if (IsPlainFile(Path.Combine(root, file)))
            {
                files.Add(file);
            }
        }

        foreach (string folder in FilePolicyService.ProtectedFolders)
        {
            string path = Path.Combine(root, folder);
            if (Directory.Exists(path) && !IsLink(path))
            {
                Collect(root, path, files);
            }
        }

        return files
            .Where(file => !IsLegacyLibraryEntry(root, file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    private static void Collect(string root, string folder, List<string> files)
    {
        foreach (string file in Directory.GetFiles(folder))
        {
            if (!IsLink(file))
            {
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        foreach (string child in Directory.GetDirectories(folder))
        {
            if (!IsLink(child))
            {
                Collect(root, child, files);
            }
        }
    }

    private static bool IsPlainFile(string path)
    {
        return File.Exists(path) && !IsLink(path);
    }

    private static bool IsLink(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string[] Split(string relativePath)
    {
        return relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
