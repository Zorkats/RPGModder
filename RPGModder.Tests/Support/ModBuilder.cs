using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Tests.Support;

/// <summary>Writes a mod folder whose mod.json is built from a list of edits (TST-1).</summary>
public sealed class ModBuilder
{
    private readonly string _id;
    private readonly List<(string Target, byte[] Content)> _files = new();
    private readonly List<JsonPatch> _patches = new();
    private readonly List<PluginEntry> _plugins = new();
    private string _name;
    private bool _isFhmmMod;

    public ModBuilder(string id)
    {
        _id = id;
        _name = id;
    }

    /// <summary>The mod folder name, which a profile lists in EnabledMods.</summary>
    public string Id => _id;

    public ModBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public ModBuilder ReplaceFile(string target, string content)
    {
        return ReplaceFile(target, System.Text.Encoding.UTF8.GetBytes(content));
    }

    public ModBuilder ReplaceFile(string target, byte[] content)
    {
        _files.Add((target, content));
        return this;
    }

    public ModBuilder PatchJson(string target, JObject mergeData)
    {
        _patches.Add(new JsonPatch { Target = target, MergeData = mergeData });
        return this;
    }

    public ModBuilder RegisterPlugin(
        string name,
        bool status = true,
        IDictionary<string, string>? parameters = null,
        string description = "")
    {
        _plugins.Add(new PluginEntry
        {
            Name = name,
            Status = status,
            Description = description,
            Parameters = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>())
        });
        return this;
    }

    public ModBuilder AsFhmmMod()
    {
        _isFhmmMod = true;
        return this;
    }

    public string BuildInto(string modsRoot)
    {
        string modFolder = Path.Combine(modsRoot, _id);
        Directory.CreateDirectory(modFolder);

        var manifest = new ModManifest
        {
            Metadata = new ModMetadata
            {
                Id = _id,
                Name = _name,
                Author = "Zorkats",
                IsFhmmMod = _isFhmmMod
            },
            JsonPatches = _patches,
            PluginsRegistry = _plugins
        };

        foreach ((string target, byte[] content) in _files)
        {
            string source = "files/" + target;
            string sourcePath = Path.Combine(modFolder, source.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllBytes(sourcePath, content);
            manifest.FileOps.Add(new FileOperation { Source = source, Target = target });
        }

        File.WriteAllText(Path.Combine(modFolder, "mod.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented));
        return modFolder;
    }
}
