using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

public interface IProfileRepository
{
    IReadOnlyList<string> List(GameWorkspacePaths workspace);
    ModProfile Load(GameWorkspacePaths workspace, string profileName);
    void Save(GameWorkspacePaths workspace, string profileName, ModProfile profile);
    void Delete(GameWorkspacePaths workspace, string profileName);
}

public sealed class ProfileRepository : IProfileRepository
{
    public IReadOnlyList<string> List(GameWorkspacePaths workspace)
    {
        workspace.EnsureCreated();
        var names = Directory.GetFiles(workspace.Profiles, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Equals("default", StringComparison.OrdinalIgnoreCase) ? "Default" : name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!names.Contains("Default", StringComparer.OrdinalIgnoreCase))
        {
            names.Insert(0, "Default");
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

        return JsonConvert.DeserializeObject<ModProfile>(File.ReadAllText(path)) ?? new ModProfile();
    }

    public void Save(GameWorkspacePaths workspace, string profileName, ModProfile profile)
    {
        string path = workspace.GetProfilePath(profileName);
        FileTreeService.WriteAllTextAtomic(path, JsonConvert.SerializeObject(profile, Formatting.Indented));
    }

    public void Delete(GameWorkspacePaths workspace, string profileName)
    {
        if (profileName.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Default profile cannot be deleted.");
        }

        string path = workspace.GetProfilePath(profileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
