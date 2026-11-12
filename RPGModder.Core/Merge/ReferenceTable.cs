namespace RPGModder.Core.Merge;

/// <summary>
/// The reference table of guide 3.11 as rows: where an id sits in an event command (<see cref="Commands"/>) and in a
/// record field (<see cref="Fields"/>), and the text codes that name one (<see cref="TextCodes"/>).
/// <see cref="ReferenceMap"/> walks a change set once with these rows, to scan and to rewrite. <c>p[n]</c> is
/// <c>parameters[n]</c>; only values above 0 are ids. Every command row was checked against the MV and MZ core scripts.
/// </summary>
internal static class ReferenceTable
{
    private const string Map = RpgDataSchema.MapTable;
    private const string Actors = "actors.json";
    private const string Classes = "classes.json";
    private const string Skills = "skills.json";
    private const string Items = "items.json";
    private const string Weapons = "weapons.json";
    private const string Armors = "armors.json";
    private const string Enemies = "enemies.json";
    private const string States = "states.json";
    private const string Troops = "troops.json";
    private const string MapInfos = "mapinfos.json";
    private const string SystemFile = "system.json";

    /// <summary>The files whose records carry <c>traits</c>; an enemy's traits use the same codes.</summary>
    private static readonly string[] TraitFiles = [Actors, Classes, Weapons, Armors, Enemies, States];

    private static readonly CommandPlace[] CommandRows =
    [
        // Map events: rewritten in the map's own file, ambiguous anywhere else (they act on whatever map is loaded).
        Id(IdKind.MapEvent, 203, 0), // Set Event Location
        Id(IdKind.MapEvent, 203, 2, Is(1, 2)), // ... exchanged with another event
        Id(IdKind.MapEvent, 205, 0), // Set Movement Route
        Id(IdKind.MapEvent, 212, 0), // Show Animation
        Id(IdKind.MapEvent, 213, 0), // Show Balloon Icon
        Id(IdKind.MapEvent, 111, 1, Is(0, 6)), // Conditional Branch: character
        Id(IdKind.MapEvent, 122, 5, Is(3, 3), Is(4, 5)), // Control Variables: game data, character
        Id(IdKind.MapEvent, 285, 3, Is(2, 2)) with { MzOnly = true }, // Get Location Info: by character (MV: by variable)

        // Maps.
        Id(IdKind.Map, 201, 1, Is(0, 0)), // Transfer Player, direct
        Id(IdKind.Map, 202, 2, Is(1, 0)), // Set Vehicle Location, direct
        ByVariable(IdKind.Map, 201, Is(0, 1)), // Transfer Player by variable
        ByVariable(IdKind.Map, 202, Is(1, 1)), // Set Vehicle Location by variable
        new(122, 4, PlaceUse.Constant, null, [Is(3, 0)]), // Control Variables: constant, for every by-variable row
        new(122, 4, PlaceUse.RandomRange, null, [Is(3, 2)]), // Control Variables: random p[4] to p[5], likewise

        // Items.
        Id(IdKind.Item, 126, 0), // Change Items
        Id(IdKind.Item, 111, 1, Is(0, 8)),
        Id(IdKind.Item, 122, 5, Is(3, 3), Is(4, 0)),
        Id(IdKind.Item, 302, 1, Is(0, 0)), // Shop Processing
        Id(IdKind.Item, 605, 1, Is(0, 0)), // ... its further goods

        // Weapons.
        Id(IdKind.Weapon, 127, 0), // Change Weapons
        Id(IdKind.Weapon, 111, 1, Is(0, 9)),
        Id(IdKind.Weapon, 111, 3, Is(0, 4), Is(2, 4)),
        Id(IdKind.Weapon, 122, 5, Is(3, 3), Is(4, 1)),
        Id(IdKind.Weapon, 302, 1, Is(0, 1)),
        Id(IdKind.Weapon, 605, 1, Is(0, 1)),
        Id(IdKind.Weapon, 319, 2, Is(1, 1)), // Change Equipment, equip type 1

        // Armors. Equip type 2 is a weapon slot for a dual-wield actor (trait 55), so that row is a second slot.
        Id(IdKind.Armor, 128, 0), // Change Armors
        Id(IdKind.Armor, 111, 1, Is(0, 10)),
        Id(IdKind.Armor, 111, 3, Is(0, 4), Is(2, 5)),
        Id(IdKind.Armor, 122, 5, Is(3, 3), Is(4, 2)),
        Id(IdKind.Armor, 302, 1, Is(0, 2)),
        Id(IdKind.Armor, 605, 1, Is(0, 2)),
        new(319, 2, PlaceUse.Id, IdKind.Armor, [Is(1, 2)], SecondSlot: true),
        Id(IdKind.Armor, 319, 2, new ParameterTest(1, 3, int.MaxValue)),

        // Skills.
        Id(IdKind.Skill, 318, 3), // Change Skill
        Id(IdKind.Skill, 339, 2), // Force Action
        Id(IdKind.Skill, 111, 3, Is(0, 4), Is(2, 3)),

        // States.
        Id(IdKind.State, 313, 3), // Change State
        Id(IdKind.State, 333, 2), // Change Enemy State
        Id(IdKind.State, 111, 3, Is(0, 4), Is(2, 6)),
        Id(IdKind.State, 111, 3, Is(0, 5), Is(2, 1)),

        // Enemies, troops, common events.
        Id(IdKind.Enemy, 336, 1), // Enemy Transform
        Id(IdKind.Troop, 301, 1, Is(0, 0)), // Battle Processing, direct
        ByVariable(IdKind.Troop, 301, Is(0, 1)), // ... by variable
        Id(IdKind.CommonEvent, 117, 0), // Common Event

        // Actors. 311 to 318 and 326 name an actor when p[0] is 0, a variable when it is 1.
        Id(IdKind.Actor, 129, 0), // Change Party Member
        Id(IdKind.Actor, 303, 0), // Name Input Processing
        Id(IdKind.Actor, 111, 1, Is(0, 4)),
        Id(IdKind.Actor, 122, 5, Is(3, 3), Is(4, 3)),
        Id(IdKind.Actor, 311, 1, Is(0, 0)),
        Id(IdKind.Actor, 312, 1, Is(0, 0)),
        Id(IdKind.Actor, 313, 1, Is(0, 0)),
        Id(IdKind.Actor, 314, 1, Is(0, 0)),
        Id(IdKind.Actor, 315, 1, Is(0, 0)),
        Id(IdKind.Actor, 316, 1, Is(0, 0)),
        Id(IdKind.Actor, 317, 1, Is(0, 0)),
        Id(IdKind.Actor, 318, 1, Is(0, 0)),
        Id(IdKind.Actor, 326, 1, Is(0, 0)),
        ByVariable(IdKind.Actor, 311, Is(0, 1)),
        ByVariable(IdKind.Actor, 312, Is(0, 1)),
        ByVariable(IdKind.Actor, 313, Is(0, 1)),
        ByVariable(IdKind.Actor, 314, Is(0, 1)),
        ByVariable(IdKind.Actor, 315, Is(0, 1)),
        ByVariable(IdKind.Actor, 316, Is(0, 1)),
        ByVariable(IdKind.Actor, 317, Is(0, 1)),
        ByVariable(IdKind.Actor, 318, Is(0, 1)),
        ByVariable(IdKind.Actor, 326, Is(0, 1)),
        Id(IdKind.Actor, 319, 0), // Change Equipment
        Id(IdKind.Actor, 320, 0), // Change Name
        Id(IdKind.Actor, 321, 0), // Change Class
        Id(IdKind.Actor, 322, 0), // Change Actor Images
        Id(IdKind.Actor, 324, 0), // Change Nickname
        Id(IdKind.Actor, 325, 0), // Change Profile
        Id(IdKind.Actor, 339, 1, Is(0, 1)), // Force Action by an actor

        // Classes, animations, tilesets.
        Id(IdKind.Class, 321, 1),
        Id(IdKind.Class, 111, 3, Is(0, 4), Is(2, 2)),
        Id(IdKind.Animation, 212, 1), // Show Animation
        Id(IdKind.Animation, 337, 1), // Show Battle Animation
        Id(IdKind.Tileset, 282, 0), // Change Tileset

        // Free text (GR-R9): a whole number here stops a move of that id, of any kind.
        FreeText(355, 0), // Script
        FreeText(655, 0), // ... its further lines
        FreeText(356, 0), // Plugin Command (MV)
        FreeText(357, 3), // Plugin Command (MZ): the arguments
        FreeText(45, 0), // Script in a move route
        FreeText(111, 1, Is(0, 12)), // Conditional Branch: script
        FreeText(122, 4, Is(3, 4)) // Control Variables: script
    ];

    private static readonly FieldPlace[] FieldRows =
    [
        // Maps.
        new([MapInfos], "parentId", IdKind.Map),
        new([SystemFile], "startMapId", IdKind.Map),
        new([SystemFile], "boat.startMapId", IdKind.Map),
        new([SystemFile], "ship.startMapId", IdKind.Map),
        new([SystemFile], "airship.startMapId", IdKind.Map),

        // A map's own fields, and its events' page conditions.
        new([Map], "tilesetId", IdKind.Tileset),
        new([Map], "encounterList[*].troopId", IdKind.Troop),
        new([Map], "pages[*].conditions.itemId", IdKind.Item),
        new([Map], "pages[*].conditions.actorId", IdKind.Actor),

        // Database records.
        new([Actors], "classId", IdKind.Class),
        new([Actors], "equips[*]", IdKind.Weapon, MaxIndex: 0),
        new([Actors], "equips[*]", IdKind.Armor, MinIndex: 1, MaxIndex: 1, SecondSlot: true),
        new([Actors], "equips[*]", IdKind.Armor, MinIndex: 2),
        new([Classes], "learnings[*].skillId", IdKind.Skill),
        new([Skills, Items, Weapons], "animationId", IdKind.Animation),
        new([Enemies], "actions[*].skillId", IdKind.Skill),
        new([Enemies], "actions[*].conditionParam1", IdKind.State, new FieldTest("conditionType", [4])),
        new([Enemies], "dropItems[*].dataId", IdKind.Item, new FieldTest("kind", [1])),
        new([Enemies], "dropItems[*].dataId", IdKind.Weapon, new FieldTest("kind", [2])),
        new([Enemies], "dropItems[*].dataId", IdKind.Armor, new FieldTest("kind", [3])),
        new([Troops], "members[*].enemyId", IdKind.Enemy),
        new([Troops], "pages[*].conditions.actorId", IdKind.Actor),

        // Traits 13, 14 and 32 name a state; 35 (MZ), 43 and 44 a skill.
        new(TraitFiles, "traits[*].dataId", IdKind.State, new FieldTest("code", [13, 14, 32])),
        new(TraitFiles, "traits[*].dataId", IdKind.Skill, new FieldTest("code", [35, 43, 44])),

        // Effects 21 and 22 name a state, 43 a skill, 44 a common event.
        new([Skills, Items], "effects[*].dataId", IdKind.State, new FieldTest("code", [21, 22])),
        new([Skills, Items], "effects[*].dataId", IdKind.Skill, new FieldTest("code", [43])),
        new([Skills, Items], "effects[*].dataId", IdKind.CommonEvent, new FieldTest("code", [44])),

        // System: the starting party and the battle test.
        new([SystemFile], "partyMembers[*]", IdKind.Actor),
        new([SystemFile], "testBattlers[*].actorId", IdKind.Actor),
        new([SystemFile], "testBattlers[*].equips[*]", IdKind.Weapon, MaxIndex: 0),
        new([SystemFile], "testBattlers[*].equips[*]", IdKind.Armor, MinIndex: 1, MaxIndex: 1, SecondSlot: true),
        new([SystemFile], "testBattlers[*].equips[*]", IdKind.Armor, MinIndex: 2),
        new([SystemFile], "testTroopId", IdKind.Troop)
    ];

    private static readonly ILookup<int, CommandPlace> CommandsByCode = CommandRows.ToLookup(row => row.Code);

    private static readonly Dictionary<(string Table, string Pattern), FieldPlace[]> FieldsByPlace = FieldRows
        .SelectMany(row => row.Tables.Select(table => (Table: table, Row: row)))
        .GroupBy(pair => (pair.Table, pair.Row.Pattern))
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Row).ToArray());

    /// <summary>The last property name of every field pattern; a leaf under any other name is no field reference.</summary>
    private static readonly HashSet<string> FieldNames = new(FieldRows.Select(row => LastName(row.Pattern)), StringComparer.Ordinal);

    /// <summary>The rows whose deciding sibling sits at a pattern (<c>dropItems[*].kind</c> for a drop's <c>dataId</c>).</summary>
    private static readonly Dictionary<(string Table, string Pattern), FieldPlace[]> FieldsBySiblingPlace = FieldRows
        .Where(row => row.When is not null)
        .SelectMany(row => row.Tables.Select(table => (Table: table, Row: row)))
        .GroupBy(pair => (pair.Table, SiblingPattern(pair.Row)))
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Row).ToArray());

    private static readonly HashSet<string> SiblingNames =
        new(FieldRows.Where(row => row.When is not null).Select(row => row.When!.Sibling), StringComparer.Ordinal);

    /// <summary>The text codes that name an id inside message text, by their letters: <c>\N[n]</c> is an actor's name.</summary>
    public static IReadOnlyDictionary<string, IdKind> TextCodes { get; } =
        new Dictionary<string, IdKind>(StringComparer.Ordinal) { ["N"] = IdKind.Actor };

    /// <summary>
    /// The text codes with a number that the MV and MZ message windows read themselves: a variable, an actor, a party
    /// member, a color, an icon, a position and a font size. A code with a number and other letters is a plugin's,
    /// and may name any id (GR-R9).
    /// </summary>
    public static IReadOnlySet<string> BuiltInTextCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "V", "N", "P", "C", "I", "PX", "PY", "FS" };

    /// <summary>The rows for event command <paramref name="code"/>.</summary>
    public static IEnumerable<CommandPlace> Commands(int code)
    {
        return CommandsByCode[code];
    }

    /// <summary>
    /// True when a leaf named <paramref name="name"/> (or the elements of an array named so) can be a field
    /// reference, so the walker builds its path only then.
    /// </summary>
    public static bool MayBeField(string name)
    {
        return FieldNames.Contains(name);
    }

    /// <summary>The rows for a leaf of table <paramref name="table"/> whose path has the pattern <paramref name="pattern"/>.</summary>
    public static IReadOnlyList<FieldPlace> Fields(string table, string pattern)
    {
        return FieldsByPlace.TryGetValue((table, pattern), out FieldPlace[]? rows) ? rows : [];
    }

    /// <summary>True when a leaf named <paramref name="name"/> can decide the kind of a field row's id.</summary>
    public static bool MayBeSibling(string name)
    {
        return SiblingNames.Contains(name);
    }

    /// <summary>The rows whose deciding sibling is the leaf of table <paramref name="table"/> at <paramref name="pattern"/>.</summary>
    public static IReadOnlyList<FieldPlace> FieldsBySibling(string table, string pattern)
    {
        return FieldsBySiblingPlace.TryGetValue((table, pattern), out FieldPlace[]? rows) ? rows : [];
    }

    /// <summary>The property name of the id a field row reads (<c>dataId</c> for <c>dropItems[*].dataId</c>).</summary>
    public static string FieldName(FieldPlace row)
    {
        return LastName(row.Pattern);
    }

    private static CommandPlace Id(IdKind kind, int code, int index, params ParameterTest[] when)
    {
        return new CommandPlace(code, index, PlaceUse.Id, kind, when);
    }

    /// <summary>A command that names an id of <paramref name="kind"/> through a variable (3.11).</summary>
    private static CommandPlace ByVariable(IdKind kind, int code, params ParameterTest[] when)
    {
        return new CommandPlace(code, 0, PlaceUse.ByVariable, kind, when);
    }

    private static CommandPlace FreeText(int code, int index, params ParameterTest[] when)
    {
        return new CommandPlace(code, index, PlaceUse.FreeText, null, when);
    }

    private static ParameterTest Is(int index, int value)
    {
        return new ParameterTest(index, value, value);
    }

    private static string SiblingPattern(FieldPlace row)
    {
        int dot = row.Pattern.LastIndexOf('.');
        return (dot < 0 ? "" : row.Pattern[..(dot + 1)]) + row.When!.Sibling;
    }

    private static string LastName(string pattern)
    {
        string trimmed = pattern.EndsWith("[*]", StringComparison.Ordinal) ? pattern[..^3] : pattern;
        int dot = trimmed.LastIndexOf('.');
        return dot < 0 ? trimmed : trimmed[(dot + 1)..];
    }
}

/// <summary>What a place of the reference table holds.</summary>
internal enum PlaceUse
{
    /// <summary>An id of the row's kind; rewritten when that id moves.</summary>
    Id,

    /// <summary>Text a remap cannot rewrite (GR-R9).</summary>
    FreeText,

    /// <summary>A constant Control Variables assigns; with a by-variable row it may be that row's id (3.11).</summary>
    Constant,

    /// <summary>
    /// The low end of a random range Control Variables assigns, the next parameter being the high end; with a
    /// by-variable row any id in it may be that row's id.
    /// </summary>
    RandomRange,

    /// <summary>
    /// An id of the row's kind that a variable holds: a transfer or vehicle move to a map, a battle with a troop, an
    /// actor of 311 to 318 and 326.
    /// </summary>
    ByVariable
}

/// <summary>
/// One command row: parameter <c>Index</c> of command <c>Code</c>, when every test holds. <c>SecondSlot</c> marks
/// the second equip slot, which holds a weapon for a dual-wield actor. <c>MzOnly</c> marks a row only MZ reads so;
/// on any other engine the id is of no known kind.
/// </summary>
internal sealed record CommandPlace(
    int Code,
    int Index,
    PlaceUse Use,
    IdKind? Kind,
    ParameterTest[] When,
    bool SecondSlot = false,
    bool MzOnly = false);

/// <summary>Parameter <c>Index</c> holds a whole number from <c>Min</c> to <c>Max</c>.</summary>
internal readonly record struct ParameterTest(int Index, int Min, int Max);

/// <summary>
/// One field row: the leaves of <c>Tables</c> whose path pattern is <c>Pattern</c> (relative to the record, or to
/// the root of an object file). A pattern ending in <c>[*]</c> can limit the last index. <c>When</c> asks a sibling
/// property of the leaf for one of its values.
/// </summary>
internal sealed record FieldPlace(
    string[] Tables,
    string Pattern,
    IdKind Kind,
    FieldTest? When = null,
    int MinIndex = 0,
    int MaxIndex = int.MaxValue,
    bool SecondSlot = false);

/// <summary>The sibling property <c>Sibling</c> holds one of <c>Values</c>.</summary>
internal sealed record FieldTest(string Sibling, int[] Values);
