using RPGModder.Core.Platform;

namespace RPGModder.Tests.Support;

/// <summary>
/// A Steam install in a temporary folder: a library with <c>steamapps/common/&lt;installdir&gt;</c>,
/// an app manifest, and a depot cache. Nothing here touches a real Steam folder (TST-19).
/// </summary>
public sealed class SyntheticSteamLibrary
{
    public const string AppId = "4242420";
    public const string DepotId = "4242421";
    public const string ManifestId = "9000000000000000001";
    public const string BuildId = "777";
    public const string InstallDir = "Fixture Game";

    public SyntheticSteamLibrary(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common"));
        Directory.CreateDirectory(DepotCache);
    }

    public string Root { get; }

    public string SteamApps => Path.Combine(Root, "steamapps");

    public string DepotCache => Path.Combine(Root, "depotcache");

    public string InstallFolder => Path.Combine(SteamApps, "common", InstallDir);

    public string AppManifestPath => Path.Combine(SteamApps, $"appmanifest_{AppId}.acf");

    public string DepotManifestPath => Path.Combine(DepotCache, $"{DepotId}_{ManifestId}.manifest");

    /// <summary>A reader that sees only this library and records nothing outside it.</summary>
    public SteamDepotManifestReader Reader(IFileSystemOps files, IStoreLinkOpener? links = null)
    {
        return new SteamDepotManifestReader(() => new[] { Root }, files, links ?? new RecordingLinkOpener());
    }

    public void WriteAppManifest(string buildId = BuildId, long lastUpdated = 1_700_000_000, int stateFlags = 4, string manifestId = ManifestId)
    {
        File.WriteAllText(AppManifestPath, $$"""
            "AppState"
            {
            	"appid"		"{{AppId}}"
            	"Universe"		"1"
            	"name"		"Fixture Game"
            	"StateFlags"		"{{stateFlags}}"
            	"installdir"		"{{InstallDir}}"
            	"LastUpdated"		"{{lastUpdated}}"
            	"buildid"		"{{buildId}}"
            	"InstalledDepots"
            	{
            		"{{DepotId}}"
            		{
            			"manifest"		"{{manifestId}}"
            			"size"		"1000"
            		}
            	}
            }
            """);
    }

    /// <summary>Writes the depot manifest from the install folder as it stands now.</summary>
    public void WriteDepotManifestFromInstall(bool filenamesEncrypted = false)
    {
        WriteDepotManifest(SyntheticDepotWriter.EntriesOf(InstallFolder), filenamesEncrypted);
    }

    public void WriteDepotManifest(IEnumerable<SyntheticDepotWriter.Entry> entries, bool filenamesEncrypted = false, uint? extraSectionMagic = null)
    {
        File.WriteAllBytes(
            DepotManifestPath,
            SyntheticDepotWriter.Build(ulong.Parse(DepotId), ulong.Parse(ManifestId), entries, filenamesEncrypted, extraSectionMagic));
    }
}
