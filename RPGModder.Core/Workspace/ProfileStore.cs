using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using System.Diagnostics;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only reader and writer of profiles and the active-profile marker (OWN-5). Migrations run on
/// load and are written on the next save, never on read (GR-V3).
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    public const string DefaultProfileName = "Default";

    private const string SchemaVersionField = nameof(ModProfile.SchemaVersion);

    private static readonly JsonSerializerSettings ReadSettings = new() { DateParseHandling = DateParseHandling.None };

    private readonly IFileSystemOps _files;

    public ProfileStore()
        : this(new FileSystemOps())
    {
    }

    public ProfileStore(IFileSystemOps files)
    {
        _files = files;
    }

    public IReadOnlyList<string> List(GameWorkspacePaths workspace)
    {
        workspace.EnsureCreated();
        var names = Directory.GetFiles(workspace.Profiles, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Equals("default", StringComparison.OrdinalIgnoreCase) ? DefaultProfileName : name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!names.Contains(DefaultProfileName, StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, DefaultProfileName);
        }

        return names;
    }

    public ModProfile Load(GameWorkspacePaths workspace, string profileName)
    {
        string path = workspace.GetProfilePath(profileName);
        if (!File.Exists(path))
        {
            return new ModProfile();
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Reads profile JSON at any version this build knows, and a newer one as far as its fields allow.</summary>
    public static ModProfile Parse(string json)
    {
        JToken? token = JsonConvert.DeserializeObject<JToken>(json, ReadSettings);
        if (token == null || token.Type == JTokenType.Null)
        {
            return new ModProfile();
        }

        int version = ReadVersion(token);
        ModProfile profile = token.ToObject<ModProfile>(JsonSerializer.Create(ReadSettings)) ?? new ModProfile();
        profile.SchemaVersion = version;
        profile.EnabledMods ??= new List<string>();
        profile.LoadOrder ??= new List<string>();
        if (version < 2)
        {
            MigrateToVersion2(profile);
        }

        profile.Resolutions ??= new List<ProfileResolution>();
        profile.Options ??= new ProfileOptions();
        return profile;
    }

    public bool CanWrite(GameWorkspacePaths workspace, string profileName, ModProfile profile)
    {
        return profile.IsSupported && StoredVersion(workspace.GetProfilePath(profileName)) <= ModProfile.CurrentSchemaVersion;
    }

    public void Save(GameWorkspacePaths workspace, string profileName, ModProfile profile)
    {
        if (!CanWrite(workspace, profileName, profile))
        {
            throw new InvalidDataException(
                $"The profile {profileName} was written by a newer version of RPGModder and is not modified.");
        }

        JObject json = JObject.FromObject(profile);
        json[SchemaVersionField] = ModProfile.CurrentSchemaVersion;
        _files.WriteAllTextAtomic(workspace.GetProfilePath(profileName), json.ToString(Formatting.Indented));
    }

    public void Delete(GameWorkspacePaths workspace, string profileName)
    {
        if (profileName.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Default profile cannot be deleted.");
        }

        _files.DeleteFile(workspace.GetProfilePath(profileName));
    }

    public string ReadActive(GameWorkspacePaths workspace)
    {
        string markerPath = workspace.ActiveProfileMarker;
        if (!File.Exists(markerPath))
        {
            return DefaultProfileName;
        }

        try
        {
            return SafePathService.ValidateDirectoryName(File.ReadAllText(markerPath).Trim(), "Active profile marker");
        }
        catch (InvalidDataException ex)
        {
            // A marker that does not name a valid profile cannot select one; Default is the profile
            // every workspace has.
            Trace.TraceWarning($"The active profile marker is not a valid profile name and Default is used: {ex.Message}");
            return DefaultProfileName;
        }
    }

    public void WriteActive(GameWorkspacePaths workspace, string profileName)
    {
        string name = SafePathService.ValidateDirectoryName(profileName, "Profile name");
        _files.WriteAllTextAtomic(workspace.ActiveProfileMarker, name);
    }

    // GR-V3: version 2 adds collision answers, game rules and deploy options, all empty.
    private static void MigrateToVersion2(ModProfile profile)
    {
        profile.Resolutions = new List<ProfileResolution>();
        profile.Rules = null;
        profile.Options = new ProfileOptions();
        profile.SchemaVersion = 2;
    }

    private static int ReadVersion(JToken token)
    {
        // GR-V1: a profile without the field is version 1.
        JToken? version = token is JObject json ? json[SchemaVersionField] : null;
        return version != null && version.Type == JTokenType.Integer ? version.Value<int>() : 1;
    }

    private static int StoredVersion(string path)
    {
        if (!File.Exists(path))
        {
            return ModProfile.CurrentSchemaVersion;
        }

        try
        {
            return ReadVersion(JsonConvert.DeserializeObject<JToken>(File.ReadAllText(path), ReadSettings) ?? JValue.CreateNull());
        }
        catch (JsonException ex)
        {
            // A file that is not JSON holds no profile to protect; the save replaces it, as 2.0.0 did.
            Trace.TraceWarning($"The profile file {path} is not valid JSON and is replaced: {ex.Message}");
            return 1;
        }
    }
}
