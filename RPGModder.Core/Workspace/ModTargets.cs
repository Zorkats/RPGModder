using System.Diagnostics;
using Newtonsoft.Json;
using RPGModder.Core.Models;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The content-relative game paths the installed mods write: file operation and patch targets,
/// and <c>js/plugins.js</c> for a mod with plugin entries. A live file that the vanilla manifest
/// does not list counts as written by RPGModder when it is one of these (GR-V7, TX-20).
/// </summary>
public static class ModTargets
{
    private static readonly string[] KnownRoots = { "data/", "js/", "img/", "audio/", "fonts/", "css/", "icon/", "movies/", "effects/" };

    public static IReadOnlySet<string> Collect(string modsRoot, bool usesWww)
    {
        var targets = new HashSet<string>(ManagedContent.PathComparer);
        if (!Directory.Exists(modsRoot))
        {
            return targets;
        }

        foreach (string folder in Directory.GetDirectories(modsRoot).OrderBy(folder => folder, StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(folder, ModStore.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                ModManifest manifest = ModStore.ReadManifestFile(manifestPath);
                targets.UnionWith(manifest.FileOps.Select(op => Normalize(op.Target, usesWww)));
                targets.UnionWith(manifest.JsonPatches.Select(patch => Normalize(patch.Target, usesWww)));
                if (manifest.PluginsRegistry.Count > 0)
                {
                    targets.Add("js/plugins.js");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A mod whose manifest cannot be read writes nothing on deploy, so it explains no file.
                Trace.TraceWarning($"The targets of {Path.GetFileName(folder)} are skipped: {ex.Message}");
            }
        }

        return targets;
    }

    /// <summary>Maps a manifest target to a content-relative path, the way the deploy does.</summary>
    public static string Normalize(string targetPath, bool usesWww)
    {
        string normalized = targetPath.Replace('\\', '/');

        if (usesWww)
        {
            int wwwIndex = normalized.LastIndexOf("www/", StringComparison.OrdinalIgnoreCase);
            if (wwwIndex >= 0)
            {
                return normalized.Substring(wwwIndex + 4);
            }
        }

        foreach (string root in KnownRoots)
        {
            int index = normalized.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized.Substring(index);
            }
        }

        return targetPath;
    }
}
