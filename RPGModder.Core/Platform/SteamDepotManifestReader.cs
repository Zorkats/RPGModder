using System.Globalization;

namespace RPGModder.Core.Platform;

/// <summary>
/// Reads Steam's own list of an installed game's files (OWN-15, TX-22): the app manifest
/// <c>steamapps/appmanifest_&lt;appid&gt;.acf</c> names each installed depot and its manifest id,
/// and <c>depotcache/&lt;depot&gt;_&lt;manifest&gt;.manifest</c> lists that depot's files with size
/// and SHA-1. It opens those two kinds of file and nothing else in a Steam folder, only reads them,
/// and never reads a login, token or account file. It is also the only type that asks the Steam
/// client to fetch files, through <c>steam://validate/&lt;appid&gt;</c> (TX-23).
/// </summary>
public sealed class SteamDepotManifestReader : IStoreManifestSource
{
    private readonly Func<IReadOnlyList<string>> _steamRoots;
    private readonly IFileSystemOps _files;
    private readonly IStoreLinkOpener _links;

    /// <param name="steamRoots">The Steam install folders, asked for only when the game sits in a Steam library.</param>
    public SteamDepotManifestReader(Func<IReadOnlyList<string>> steamRoots, IFileSystemOps files, IStoreLinkOpener links)
    {
        _steamRoots = steamRoots;
        _files = files;
        _links = links;
    }

    /// <summary>The link that asks Steam to verify a game and fetch its missing or changed files.</summary>
    public static string ValidateLink(string appId)
    {
        return $"steam://validate/{appId}";
    }

    public StoreManifestResult Read(string gameRoot, string contentRoot)
    {
        if (!TryLocateInstall(Path.GetFullPath(gameRoot), out string steamApps, out string installRoot))
        {
            return StoreManifestResult.Unavailable(NoStoreManifestSource.NotAStoreInstall);
        }

        StoreApp? app = null;
        try
        {
            app = ReadAppManifest(steamApps, Path.GetFileName(installRoot));
            if (app == null)
            {
                return StoreManifestResult.Unavailable("No Steam app manifest names this install folder.");
            }

            if (app.Depots.Count == 0)
            {
                return StoreManifestResult.Unavailable("The Steam app manifest lists no installed depot.", app);
            }

            StringComparer comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var files = new Dictionary<string, StoreFileEntry>(comparer);
            string library = Path.GetDirectoryName(steamApps)!;
            foreach (StoreDepot depot in app.Depots)
            {
                string? manifestPath = FindDepotManifest(depot, library);
                if (manifestPath == null)
                {
                    return StoreManifestResult.Unavailable($"The manifest of depot {depot.DepotId} is not in the depot cache.", app);
                }

                SteamDepotManifest manifest = SteamDepotManifest.Parse(_files.ReadAllBytes(manifestPath));
                if (manifest.DepotId.ToString(CultureInfo.InvariantCulture) != depot.DepotId ||
                    manifest.ManifestId.ToString(CultureInfo.InvariantCulture) != depot.ManifestId)
                {
                    return StoreManifestResult.Unavailable($"The manifest file of depot {depot.DepotId} names another depot or manifest.", app);
                }

                if (manifest.FilenamesEncrypted)
                {
                    return StoreManifestResult.Unavailable($"The file names of depot {depot.DepotId} are encrypted.", app);
                }

                string? conflict = AddEntries(manifest, installRoot, Path.GetFullPath(contentRoot), files);
                if (conflict != null)
                {
                    return StoreManifestResult.Unavailable(conflict, app);
                }
            }

            return StoreManifestResult.Available(app, files.Values.ToList(), comparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StoreManifestResult.Unavailable($"The Steam manifests could not be read: {ex.Message}", app);
        }
    }

    public bool RequestFetch(string appId)
    {
        if (string.IsNullOrEmpty(appId) || !appId.All(char.IsAsciiDigit))
        {
            return false;
        }

        return _links.Open(ValidateLink(appId));
    }

    // A Steam game lives at <library>/steamapps/common/<installdir>; the game root may be that folder or below it.
    private static bool TryLocateInstall(string gameRoot, out string steamApps, out string installRoot)
    {
        for (DirectoryInfo? folder = new(gameRoot); folder?.Parent?.Parent != null; folder = folder.Parent)
        {
            DirectoryInfo common = folder.Parent!;
            DirectoryInfo apps = common.Parent!;
            if (common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                apps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) &&
                apps.Parent != null)
            {
                steamApps = apps.FullName;
                installRoot = folder.FullName;
                return true;
            }
        }

        steamApps = string.Empty;
        installRoot = string.Empty;
        return false;
    }

    private StoreApp? ReadAppManifest(string steamApps, string installDir)
    {
        StoreApp? found = null;
        foreach (string path in _files.ListFiles(steamApps, "appmanifest_*.acf").OrderBy(path => path, StringComparer.Ordinal))
        {
            (string key, SteamKeyValues root) = SteamKeyValues.Parse(_files.ReadAllText(path));
            if (!key.Equals("AppState", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.Value("installdir"), installDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found != null)
            {
                throw new InvalidDataException($"Two Steam app manifests name the install folder '{installDir}'.");
            }

            found = ToApp(root, path);
        }

        return found;
    }

    private static StoreApp ToApp(SteamKeyValues root, string path)
    {
        string appId = Digits(root.Value("appid"), "appid") ?? throw new InvalidDataException("The app manifest has no appid.");
        string? buildId = Digits(root.Value("buildid"), "buildid");
        string? lastUpdated = Digits(root.Value("LastUpdated"), "LastUpdated");
        string? stateFlags = Digits(root.Value("StateFlags"), "StateFlags");

        var depots = new List<StoreDepot>();
        SteamKeyValues? installed = root.Block("InstalledDepots");
        foreach (string depotId in installed?.BlockNames ?? Array.Empty<string>())
        {
            Digits(depotId, "depot id");
            string manifestId = Digits(installed!.Block(depotId)!.Value("manifest"), "manifest")
                                ?? throw new InvalidDataException($"Installed depot {depotId} names no manifest.");
            depots.Add(new StoreDepot(depotId, manifestId));
        }

        return new StoreApp(
            appId,
            buildId,
            lastUpdated == null ? null : DateTimeOffset.FromUnixTimeSeconds(long.Parse(lastUpdated, CultureInfo.InvariantCulture)),
            stateFlags == null ? 0 : int.Parse(stateFlags, CultureInfo.InvariantCulture),
            path,
            depots);
    }

    private static string? Digits(string? value, string name)
    {
        if (value == null)
        {
            return null;
        }

        if (value.Length == 0 || value.Length > 19 || !value.All(char.IsAsciiDigit))
        {
            throw new InvalidDataException($"The app manifest's {name} is not a number: '{value}'.");
        }

        return value;
    }

    private string? FindDepotManifest(StoreDepot depot, string library)
    {
        string fileName = $"{depot.DepotId}_{depot.ManifestId}.manifest";
        IEnumerable<string> roots = _steamRoots().Append(library).Distinct(StringComparer.OrdinalIgnoreCase);
        return roots
            .Select(root => Path.Combine(root, "depotcache", fileName))
            .FirstOrDefault(_files.FileExists);
    }

    // Maps depot names to the content root. Entries outside it are not game content RPGModder manages.
    private static string? AddEntries(
        SteamDepotManifest manifest,
        string installRoot,
        string contentRoot,
        Dictionary<string, StoreFileEntry> files)
    {
        foreach (SteamDepotManifest.Entry entry in manifest.Entries)
        {
            string name = entry.Name.Replace('\\', '/');
            string[] segments = name.Split('/');
            if (Path.IsPathRooted(name) || segments.Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException($"A depot file name is not a plain relative path: {entry.Name}");
            }

            string fullPath = Path.GetFullPath(Path.Combine(installRoot, Path.Combine(segments)));
            string relative = Path.GetRelativePath(contentRoot, fullPath);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }

            var file = new StoreFileEntry(relative.Replace('\\', '/'), entry.Size, entry.Sha1);
            if (files.TryGetValue(file.Path, out StoreFileEntry? existing))
            {
                if (existing.Size != file.Size || existing.Sha1 != file.Sha1)
                {
                    return $"Two depots list different content for {file.Path}.";
                }

                continue;
            }

            files.Add(file.Path, file);
        }

        return null;
    }
}
