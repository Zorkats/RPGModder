using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace RPGModder.Tests.Merge;

/// <summary>Small builders for the merge unit tests. Content is invented (TST-2).</summary>
internal static class MergeTestKit
{
    public const string Items = "data/Items.json";
    public const string Classes = "data/Classes.json";
    public const string SystemFile = "data/System.json";

    /// <summary>A data file the schema table does not name; its records are found from vanilla's own shape.</summary>
    public const string Quests = "data/Quests.json";

    public const string VanillaItems =
        "[\nnull,\n" +
        "{\"id\":1,\"name\":\"Tonic\",\"price\":50,\"note\":\"\",\"effects\":[{\"code\":11,\"dataId\":0,\"value1\":0,\"value2\":100}]},\n" +
        "{\"id\":2,\"name\":\"Ether\",\"price\":200,\"note\":\"\",\"effects\":[]}\n" +
        "]";

    public const string VanillaSystem =
        "{\"gameTitle\":\"Sample\",\"versionId\":11,\"editMapId\":1,\"screenWidth\":816,\"currencyUnit\":\"G\"," +
        "\"switches\":[\"\",\"Door\",\"Lever\"],\"terms\":{\"basic\":[\"Level\",\"Lv\"],\"messages\":{\"victory\":\"%1 won!\"}}," +
        "\"partyMembers\":[1]}";

    public const string VanillaQuests =
        "[null,{\"id\":1,\"name\":\"Find\",\"reward\":5},{\"id\":2,\"name\":\"Bring\",\"reward\":9}]";

    public const string Map = "data/Map001.json";
    public const string PluginList = "js/plugins.js";

    /// <summary>A 2 by 2 map with two events; each has one page with a command list and a move route.</summary>
    public const string VanillaMap =
        "{\"displayName\":\"Field\",\"width\":2,\"height\":2,\"bgm\":{\"name\":\"Theme\",\"volume\":90},\"data\":[1,2,3,4]," +
        "\"encounterList\":[],\"events\":[null," +
        "{\"id\":1,\"name\":\"Door\",\"note\":\"\",\"x\":0,\"y\":0,\"pages\":[{\"conditions\":{\"switch1Id\":1,\"switch1Valid\":false}," +
        "\"trigger\":0,\"moveRoute\":{\"list\":[{\"code\":0}],\"repeat\":true}," +
        "\"list\":[{\"code\":101,\"indent\":0,\"parameters\":[\"\",0,0,2]},{\"code\":0,\"indent\":0,\"parameters\":[]}]}]}," +
        "{\"id\":2,\"name\":\"Chest\",\"note\":\"\",\"x\":1,\"y\":1,\"pages\":[{\"conditions\":{\"switch1Id\":1,\"switch1Valid\":false}," +
        "\"trigger\":0,\"moveRoute\":{\"list\":[{\"code\":0}],\"repeat\":true},\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}]}]}" +
        "]}";

    public static readonly string VanillaPlugins = PluginsJs(
        Plugin("Alpha", "{\"speed\":\"1\",\"Font Size\":\"20\"}"),
        Plugin("Omega"));

    public static string EditMap(Action<JObject> edit)
    {
        var map = JObject.Parse(VanillaMap);
        edit(map);
        return map.ToString();
    }

    /// <summary>A <c>plugins.js</c> in the editor's own layout, with LF line ends.</summary>
    public static string PluginsJs(params string[] entries)
    {
        string list = entries.Length == 0 ? "[]" : "[\n" + string.Join(",\n", entries) + "\n]";
        return PluginListText.DefaultPrefix + list + ";\n";
    }

    public static string Plugin(string name, string parameters = "{}")
    {
        return "{\"name\":\"" + name + "\",\"status\":true,\"description\":\"\",\"parameters\":" + parameters + "}";
    }

    public static PluginEntry Entry(string name, params (string Key, string Value)[] parameters)
    {
        return new PluginEntry
        {
            Name = name,
            Parameters = parameters.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
    }

    public static ModSource RegistryMod(string modId, int priority, params PluginEntry[] plugins)
    {
        return new ModSource(modId, priority, [], [], plugins);
    }

    public static BaseFile Base(string path, string text)
    {
        return new BaseFile(path, text, Hash(text));
    }

    public static BaseFileSet Bases(params BaseFile[] files)
    {
        return BaseFileSet.From(files);
    }

    public static ModSource FileMod(string modId, string target, string text, int priority = 0)
    {
        return new ModSource(
            modId,
            priority,
            [new ModFile(target, "files/" + Path.GetFileName(target), text, Hash(text))],
            [],
            []);
    }

    public static ModSource PatchMod(string modId, string target, string mergeData, int priority = 0)
    {
        return new ModSource(modId, priority, [], [new ModPatch(target, JObject.Parse(mergeData))], []);
    }

    /// <summary>Vanilla items with one item edited through <paramref name="edit"/>.</summary>
    public static string EditItems(Action<JArray> edit)
    {
        var items = JArray.Parse(VanillaItems);
        edit(items);
        return items.ToString();
    }

    public static string EditArray(string json, Action<JArray> edit)
    {
        var array = JArray.Parse(json);
        edit(array);
        return array.ToString();
    }

    public static string EditSystem(Action<JObject> edit)
    {
        var system = JObject.Parse(VanillaSystem);
        edit(system);
        return system.ToString();
    }

    public static ModChangeSet Build(ModSource mod, BaseFileSet bases)
    {
        return ChangeSetBuilder.Build(mod, bases);
    }

    public static MergePlan Compose(BaseFileSet bases, IReadOnlyList<Resolution> resolutions, params ModSource[] mods)
    {
        return Composer.Compose(mods.Select(mod => ChangeSetBuilder.Build(mod, bases)).ToList(), resolutions, bases);
    }

    public static MergePlan Compose(BaseFileSet bases, params ModSource[] mods)
    {
        return Compose(bases, [], mods);
    }

    public static string Describe(ChangeOp op)
    {
        string value = op.Value is null ? "-" : op.Value.ToString(Newtonsoft.Json.Formatting.None);
        return $"{op.Key.File}|{op.Key.Record}|{op.Path}|{op.Kind}|{value}";
    }

    public static string Hash(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
