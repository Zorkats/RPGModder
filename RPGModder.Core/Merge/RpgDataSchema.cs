using Newtonsoft.Json.Linq;
using RPGModder.Core.Services;
using System.Globalization;

namespace RPGModder.Core.Merge;

/// <summary>
/// The only place that maps a file or a path to a policy (GR-K1). The tables follow guide 3.3; an array the
/// tables do not name is Atomic and an object they do not name is compared leaf by leaf (GR-K2).
/// </summary>
public static class RpgDataSchema
{
    /// <summary>Raised whenever a table change makes an old change set mean something else (GR-M11).</summary>
    public const int SchemaVersion = 1;

    /// <summary>The file that carries <see cref="VersionIdField"/> (C-7).</summary>
    internal const string SystemFileKey = "data/system.json";

    /// <summary>The field the game reads to tell its saves apart; the writer stamps it (C-7).</summary>
    internal const string VersionIdField = "versionId";

    /// <summary>The plugin list (guide 3.3).</summary>
    internal const string PluginListKey = "js/plugins.js";

    /// <summary>The map property that holds the events (guide 3.3).</summary>
    internal const string EventsProperty = "events";

    /// <summary>The map tree, whose record index is the map id (guide 3.11).</summary>
    internal const string MapInfosKey = "data/mapinfos.json";

    /// <summary>
    /// The highest map id the MV editor gives (GR-R6). Also used when the engine is unknown, because it is the lower
    /// of the two.
    /// </summary>
    private const int MvMapIdLimit = 999;

    /// <summary>The highest map id the MZ 1.10.0 editor gives (GR-R6; its map tree's <c>maxMapId</c>).</summary>
    private const int MzMapIdLimit = 2000;

    /// <summary>
    /// The highest event id per map (GR-R6). The MZ 1.10.0 editor refuses event 1000 ("Cannot create any more
    /// events."); MV's limit is taken to be the same.
    /// </summary>
    private const int MapEventIdLimit = 999;

    /// <summary>The config save forms 2a decodes (GR-M10); <c>config.save</c> gives <c>merge.config_save_unsupported</c>.</summary>
    private const string MvConfigSave = "config.rpgsave";

    private const string MzConfigSave = "config.rmmzsave";

    private const string SystemFile = "system.json";
    private const string MapInfosFile = "mapinfos.json";
    private const string MapPrefix = "data/map";
    private const string JsonSuffix = ".json";

    /// <summary>The policy table name every <c>MapNNN.json</c> shares; no real file name has angle brackets.</summary>
    private const string MapTable = "<map>";

    private const string PagesPath = "pages";

    private static readonly HashSet<string> ConfigSaveNames = new(StringComparer.Ordinal)
    {
        MvConfigSave,
        MzConfigSave,
        "config.save"
    };

    /// <summary>The database files of guide 3.3: an array whose index is the record id.</summary>
    private static readonly HashSet<string> RecordFiles = new(StringComparer.Ordinal)
    {
        "actors.json",
        "classes.json",
        "skills.json",
        "items.json",
        "weapons.json",
        "armors.json",
        "enemies.json",
        "states.json",
        "animations.json",
        "tilesets.json",
        "troops.json",
        "commonevents.json",
        MapInfosFile
    };

    /// <summary>Array policies by file name and by path pattern relative to the record (or to the root of an object file).</summary>
    private static readonly Dictionary<string, Dictionary<string, ArrayPolicy>> Policies = new(StringComparer.Ordinal)
    {
        [MapTable] = new(StringComparer.Ordinal)
        {
            // Map-level, on record *.
            ["data"] = ArrayPolicy.Positional,
            ["encounterList"] = ArrayPolicy.Sequence,

            // Event-level, relative to event:<id>.
            [PagesPath] = ArrayPolicy.Positional,
            ["pages[*].list"] = ArrayPolicy.Commands
        },
        ["actors.json"] = new(StringComparer.Ordinal)
        {
            ["traits"] = ArrayPolicy.Sequence
        },
        ["classes.json"] = new(StringComparer.Ordinal)
        {
            ["params"] = ArrayPolicy.Positional,
            ["params[*]"] = ArrayPolicy.Positional,
            ["learnings"] = ArrayPolicy.Sequence,
            ["traits"] = ArrayPolicy.Sequence
        },
        ["skills.json"] = new(StringComparer.Ordinal)
        {
            ["effects"] = ArrayPolicy.Sequence
        },
        ["items.json"] = new(StringComparer.Ordinal)
        {
            ["effects"] = ArrayPolicy.Sequence
        },
        ["weapons.json"] = new(StringComparer.Ordinal)
        {
            ["traits"] = ArrayPolicy.Sequence
        },
        ["armors.json"] = new(StringComparer.Ordinal)
        {
            ["traits"] = ArrayPolicy.Sequence
        },
        ["enemies.json"] = new(StringComparer.Ordinal)
        {
            ["actions"] = ArrayPolicy.Sequence,
            ["dropItems"] = ArrayPolicy.Positional,
            ["traits"] = ArrayPolicy.Sequence
        },
        ["states.json"] = new(StringComparer.Ordinal)
        {
            ["traits"] = ArrayPolicy.Sequence
        },
        ["tilesets.json"] = new(StringComparer.Ordinal)
        {
            ["flags"] = ArrayPolicy.Positional
        },
        ["troops.json"] = new(StringComparer.Ordinal)
        {
            ["members"] = ArrayPolicy.Sequence,
            ["pages"] = ArrayPolicy.Positional,
            ["pages[*].list"] = ArrayPolicy.Commands
        },
        ["commonevents.json"] = new(StringComparer.Ordinal)
        {
            ["list"] = ArrayPolicy.Commands
        },
        [SystemFile] = new(StringComparer.Ordinal)
        {
            ["switches"] = ArrayPolicy.Keyed,
            ["variables"] = ArrayPolicy.Keyed,
            ["elements"] = ArrayPolicy.Keyed,
            ["skillTypes"] = ArrayPolicy.Keyed,
            ["weaponTypes"] = ArrayPolicy.Keyed,
            ["armorTypes"] = ArrayPolicy.Keyed,
            ["equipTypes"] = ArrayPolicy.Keyed,
            ["terms.basic"] = ArrayPolicy.Positional,
            ["terms.commands"] = ArrayPolicy.Positional,
            ["terms.params"] = ArrayPolicy.Positional,
            ["sounds"] = ArrayPolicy.Positional
        }
    };

    /// <summary>Objects that are one value, by table and path pattern (guide 3.3: an event page's <c>moveRoute</c>).</summary>
    private static readonly Dictionary<string, HashSet<string>> AtomicObjects = new(StringComparer.Ordinal)
    {
        [MapTable] = new(StringComparer.Ordinal) { "pages[*].moveRoute" }
    };

    /// <summary>
    /// Editor and display fields of <c>System.json</c> that never produce ops (GR-M5), at any depth, because MZ
    /// moves the screen fields into <c>advanced</c>.
    /// </summary>
    private static readonly HashSet<string> SystemVolatile = new(StringComparer.Ordinal)
    {
        VersionIdField,
        "editMapId",
        "screenWidth",
        "screenHeight",
        "uiAreaWidth",
        "uiAreaHeight",
        "windowLineWidth",
        "locale",
        "title1Name",
        "title2Name"
    };

    /// <summary>Editor tree state of <c>MapInfos.json</c> records that never produces ops (GR-M5).</summary>
    private static readonly HashSet<string> MapInfosVolatile = new(StringComparer.Ordinal)
    {
        "scrollX",
        "scrollY",
        "expanded"
    };

    /// <summary>
    /// Whether lane A reads a file's text. A <c>.json</c> file directly inside <c>data/</c> is data,
    /// <c>js/plugins.js</c> is the plugin list, a <c>config</c> save is a config save, everything else is bytes.
    /// </summary>
    public static MergeFileKind KindOf(string path)
    {
        string key = MergePath.ToKey(path);
        if (key == PluginListKey)
        {
            return MergeFileKind.PluginList;
        }

        int slash = key.LastIndexOf('/');
        string name = slash < 0 ? key : key[(slash + 1)..];
        if (ConfigSaveNames.Contains(name))
        {
            return MergeFileKind.ConfigSave;
        }

        if (slash == "data".Length && key.StartsWith("data/", StringComparison.Ordinal) && name.EndsWith(JsonSuffix, StringComparison.Ordinal))
        {
            return MergeFileKind.Data;
        }

        return MergeFileKind.Opaque;
    }

    /// <summary>The root shape of a data file, from the table and, for a file the table does not name, from vanilla's own shape.</summary>
    internal static RootShape ShapeOf(string fileKey, JToken vanillaRoot)
    {
        string name = FileName(fileKey);
        if (RecordFiles.Contains(name) && vanillaRoot is JArray)
        {
            return RootShape.Records;
        }

        if (IsMapFile(fileKey) && vanillaRoot is JObject)
        {
            return RootShape.Map;
        }

        if (vanillaRoot is JArray array && LooksLikeRecords(array))
        {
            return RootShape.Records;
        }

        return vanillaRoot is JObject ? RootShape.Object : RootShape.Atomic;
    }

    /// <summary>
    /// The root type the game needs for a file the table names: an object for <c>System.json</c> and a map, an
    /// array for a database file. <c>null</c> for any other file, whose root may be anything.
    /// </summary>
    internal static JTokenType? ExpectedRootOf(string fileKey)
    {
        string name = FileName(fileKey);
        if (name == SystemFile || IsMapFile(fileKey))
        {
            return JTokenType.Object;
        }

        return RecordFiles.Contains(name) ? JTokenType.Array : null;
    }

    internal static ArrayPolicy PolicyOf(string fileKey, string path)
    {
        if (Policies.TryGetValue(TableOf(fileKey), out Dictionary<string, ArrayPolicy>? table) &&
            table.TryGetValue(MergePath.Pattern(path), out ArrayPolicy policy))
        {
            return policy;
        }

        return ArrayPolicy.Atomic;
    }

    /// <summary>True when the object at <paramref name="path"/> is one value, not compared leaf by leaf.</summary>
    internal static bool IsAtomicObject(string fileKey, string path)
    {
        return AtomicObjects.TryGetValue(TableOf(fileKey), out HashSet<string>? paths) && paths.Contains(MergePath.Pattern(path));
    }

    /// <summary><c>data/MapNNN.json</c>: <c>map</c>, then one or more digits.</summary>
    internal static bool IsMapFile(string fileKey)
    {
        if (!fileKey.StartsWith(MapPrefix, StringComparison.Ordinal) ||
            !fileKey.EndsWith(JsonSuffix, StringComparison.Ordinal) ||
            fileKey.Length <= MapPrefix.Length + JsonSuffix.Length)
        {
            return false;
        }

        foreach (char current in fileKey.AsSpan(MapPrefix.Length, fileKey.Length - MapPrefix.Length - JsonSuffix.Length))
        {
            if (!char.IsAsciiDigit(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The map id of <c>data/mapNNN.json</c> in the one form the game loads: at least three digits, no extra
    /// leading zero, an id from 1 up.
    /// </summary>
    internal static bool TryGetMapId(string fileKey, out int id)
    {
        id = 0;
        return IsMapFile(fileKey) &&
               int.TryParse(fileKey.AsSpan(MapPrefix.Length, fileKey.Length - MapPrefix.Length - JsonSuffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
               id > 0 &&
               MapKey(id) == fileKey;
    }

    /// <summary>The key of map <paramref name="id"/>, the name <c>DataManager.loadMapData</c> builds, lower-cased.</summary>
    internal static string MapKey(int id)
    {
        return MapPrefix + id.ToString("D3", CultureInfo.InvariantCulture) + JsonSuffix;
    }

    /// <summary>The casing the game asks for, for a map file lane B renders as text (seam 3.3, <c>DiskPath</c>).</summary>
    internal static string? DiskPathOf(string fileKey)
    {
        return TryGetMapId(fileKey, out int id) ? "data/Map" + id.ToString("D3", CultureInfo.InvariantCulture) + JsonSuffix : null;
    }

    /// <summary>
    /// The highest id a remap may give, or <c>null</c> for a kind step 1 does not remap (GR-R6). A <c>null</c>
    /// engine gets MV's limits.
    /// </summary>
    internal static int? RemapLimit(IdKind kind, RpgMakerEngine? engine)
    {
        return kind switch
        {
            IdKind.Map => engine == RpgMakerEngine.MZ ? MzMapIdLimit : MvMapIdLimit,
            IdKind.MapEvent => MapEventIdLimit,
            _ => null
        };
    }

    /// <summary>
    /// A page setting of a map event (seam 11.1): the <c>pages</c> list itself, or anything under one page except
    /// its command list, which is a <c>LogicClash</c> place.
    /// </summary>
    internal static bool IsPageSetting(RecordKey key, string path)
    {
        if (!IsMapFile(key.File) || !RecordKey.TryGetEvent(key.Record, out _) || MergePath.IsPseudoLeaf(path))
        {
            return false;
        }

        IReadOnlyList<PathSegment> segments = MergePath.Parse(path);
        if (segments.Count == 0 || segments[0].Name != PagesPath)
        {
            return false;
        }

        return segments.Count < 3 || segments[1].Name is not null || segments[2].Name != "list";
    }

    /// <summary>The config save forms 2a decodes. Only <see cref="MergeFileKind.ConfigSave"/> files are asked.</summary>
    internal static bool IsDecodedConfigSave(string fileKey)
    {
        string name = FileName(fileKey);
        return name is MvConfigSave or MzConfigSave;
    }

    /// <summary>The MZ form, a zlib byte string; <see cref="ConfigSaveText"/> picks its codec by this.</summary>
    internal static bool IsMzConfigSave(string fileKey)
    {
        return FileName(fileKey) == MzConfigSave;
    }

    /// <summary>
    /// The events list of a map is a record list when every entry is <c>null</c> or an object whose <c>id</c> is
    /// its index (seam 11.1); a list of only <c>null</c> counts, because a map without events is still a map.
    /// </summary>
    internal static bool IsEventList(JToken? events)
    {
        return events is JArray array && (array.All(element => element.Type == JTokenType.Null) || LooksLikeRecords(array));
    }

    /// <summary>True when the property <paramref name="propertyName"/> under <paramref name="parentPath"/> never produces an op.</summary>
    internal static bool IsVolatile(string fileKey, string parentPath, string propertyName)
    {
        string name = FileName(fileKey);
        if (name == SystemFile)
        {
            return SystemVolatile.Contains(propertyName);
        }

        return name == MapInfosFile && parentPath.Length == 0 && MapInfosVolatile.Contains(propertyName);
    }

    /// <summary>Guide 3.3, last data row: elements that are <c>null</c> or objects whose <c>id</c> equals their index.</summary>
    private static bool LooksLikeRecords(JArray array)
    {
        bool anyRecord = false;
        for (int index = 0; index < array.Count; index++)
        {
            JToken element = array[index];
            if (element.Type == JTokenType.Null)
            {
                continue;
            }

            // An id too large for a long is held as a BigInteger; it matches no index, so the array is not records.
            if (element is not JObject record ||
                record["id"] is not JValue { Type: JTokenType.Integer, Value: long id } ||
                id != index)
            {
                return false;
            }

            anyRecord = true;
        }

        return anyRecord;
    }

    private static string TableOf(string fileKey)
    {
        return IsMapFile(fileKey) ? MapTable : FileName(fileKey);
    }

    private static string FileName(string fileKey)
    {
        int slash = fileKey.LastIndexOf('/');
        return slash < 0 ? fileKey : fileKey[(slash + 1)..];
    }
}

internal enum RootShape
{
    Records,
    Map,
    Object,
    Atomic
}

internal enum ArrayPolicy
{
    Keyed,
    Positional,
    Sequence,
    Commands,
    Atomic
}
