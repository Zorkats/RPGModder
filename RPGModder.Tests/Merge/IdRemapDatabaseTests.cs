using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

/// <summary>TST-15, step 2: database records. Content is invented (TST-2).</summary>
public sealed class IdRemapDatabaseTests
{
    private const string Generic = "[null,{\"id\":1,\"name\":\"One\"},{\"id\":2,\"name\":\"Two\"}]";

    private static readonly Dictionary<IdKind, string> FileOfKind = new()
    {
        [IdKind.Item] = "data/Items.json",
        [IdKind.Weapon] = "data/Weapons.json",
        [IdKind.Armor] = "data/Armors.json",
        [IdKind.Skill] = "data/Skills.json",
        [IdKind.State] = "data/States.json",
        [IdKind.Enemy] = "data/Enemies.json",
        [IdKind.Troop] = "data/Troops.json",
        [IdKind.CommonEvent] = "data/CommonEvents.json",
        [IdKind.Actor] = "data/Actors.json",
        [IdKind.Class] = "data/Classes.json",
        [IdKind.Animation] = "data/Animations.json",
        [IdKind.Tileset] = "data/Tilesets.json"
    };

    /// <summary>
    /// A game: the twelve database files with two records each and no trait 55 (so no actor dual-wields), the map
    /// and System. <c>overrides</c> replace a file's text, or drop the file when the text is <c>null</c>.
    /// </summary>
    private sealed class Game
    {
        private readonly Dictionary<string, string> _vanilla = new(StringComparer.Ordinal);

        public Game(params (string Path, string? Text)[] overrides)
        {
            foreach (string file in FileOfKind.Values)
            {
                _vanilla[file] = Generic;
            }

            _vanilla[Map] = VanillaMap;
            _vanilla[SystemFile] = VanillaSystem;
            foreach ((string path, string? text) in overrides)
            {
                if (text is null)
                {
                    _vanilla.Remove(path);
                }
                else
                {
                    _vanilla[path] = text;
                }
            }

            Bases = MergeTestKit.Bases(_vanilla.Select(pair => Base(pair.Key, pair.Value)).ToArray());
        }

        public BaseFileSet Bases { get; }

        /// <summary>
        /// A mod whose files are vanilla's with <paramref name="edit"/> applied; it ships the files it touched. A file
        /// vanilla lacks starts as the vanilla map for a map file and as an empty database file otherwise.
        /// </summary>
        public RemapMod Mod(string modId, int priority, Action<Func<string, JToken>> edit, params string[] scripts)
        {
            var files = new Dictionary<string, JToken>(StringComparer.Ordinal);
            edit(path =>
            {
                if (!files.TryGetValue(path, out JToken? root))
                {
                    string? text = _vanilla.GetValueOrDefault(path);
                    root = JToken.Parse(text ?? (path.StartsWith("data/Map0", StringComparison.Ordinal) ? VanillaMap : "[null]"));
                    files[path] = root;
                }

                return root;
            });

            var source = new ModSource(
                modId,
                priority,
                files.Select(pair =>
                {
                    string text = pair.Value.ToString();
                    return new ModFile(pair.Key, "files/" + Path.GetFileName(pair.Key), text, Hash(text));
                }).ToList(),
                [],
                []);
            return new RemapMod(ChangeSetBuilder.Build(source, Bases), true, scripts);
        }

        /// <summary>a-tin adds record 3 "A" of <paramref name="kind"/>, b-oak adds record 3 "B" and makes its own edits.</summary>
        public (RemapMod A, RemapMod B) Clash(
            IdKind kind,
            Action<Func<string, JToken>>? editB = null,
            Action<Func<string, JToken>>? editA = null,
            params string[] scriptsB)
        {
            RemapMod a = Mod("a-tin", 0, file =>
            {
                AddRecord(file(FileOfKind[kind]), 3, "A");
                editA?.Invoke(file);
            });
            RemapMod b = Mod("b-oak", 1, file =>
            {
                AddRecord(file(FileOfKind[kind]), 3, "B");
                editB?.Invoke(file);
            }, scriptsB);
            return (a, b);
        }
    }

    private static void AddRecord(JToken file, int id, string name)
    {
        var records = (JArray)file;
        while (records.Count <= id)
        {
            records.Add(JValue.CreateNull());
        }

        records[id] = new JObject { ["id"] = id, ["name"] = name };
    }

    private static JObject Command(int code, params object[] parameters)
    {
        return new JObject { ["code"] = code, ["indent"] = 0, ["parameters"] = new JArray(parameters) };
    }

    private static Action<Func<string, JToken>> InEvent(JObject command)
    {
        return file => ((JArray)file(Map)["events"]![1]!["pages"]![0]!["list"]!).Insert(0, command.DeepClone());
    }

    /// <summary>Sets the token at <paramref name="path"/>; its last step is a property name, created when missing.</summary>
    private static Action<Func<string, JToken>> Set(string file, string path, string json)
    {
        return files =>
        {
            JToken root = files(file);
            int dot = path.LastIndexOf('.');
            var parent = (JObject)(dot < 0 ? root : root.SelectToken(path[..dot])!);
            parent[path[(dot + 1)..]] = JToken.Parse(json);
        };
    }

    private static RemapPlan Plan(Game game, params RemapMod[] mods)
    {
        return Plan(null, game, mods);
    }

    private static RemapPlan Plan(RpgMakerEngine? engine, Game game, params RemapMod[] mods)
    {
        return IdRemapPlanner.Plan(mods, [], [], game.Bases, engine);
    }

    private static MergePlan ComposeRemapped(RemapPlan plan, Game game, params RemapMod[] mods)
    {
        List<ModChangeSet> sets = mods
            .Where(mod => mod.Enabled)
            .Select(mod => IdRemapper.Apply(mod.ChangeSet, plan.Remaps))
            .ToList();
        return Composer.Compose(sets, [], game.Bases, plan);
    }

    private static JToken Written(MergePlan merged, Game game, string file)
    {
        IReadOnlyList<MergedFile> files = MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ);
        return JToken.Parse(files.Single(item => string.Equals(item.File, file, StringComparison.OrdinalIgnoreCase)).Text!);
    }

    private static string Entry(IdRemap remap)
    {
        return $"{remap.ModId}|{remap.Kind}|{remap.Scope}|{remap.FromId}>{remap.ToId}";
    }

    private static string Flat(JToken? token)
    {
        return token!.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static TheoryData<IdKind> DatabaseKinds => new(FileOfKind.Keys);

    [Theory]
    [MemberData(nameof(DatabaseKinds))]
    public void Plan_TwoModsAddingOneRecordId_MovesTheLaterModAndBothDeploy(IdKind kind)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(kind);

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal([$"b-oak|{kind}||3>4"], plan.NewEntries.Select(Entry));
        Assert.Empty(merged.Collisions);
        Assert.True(merged.IsDeployable);
        JToken records = Written(merged, game, FileOfKind[kind]);
        Assert.Equal("{\"id\":3,\"name\":\"A\"}", Flat(records[3]));
        Assert.Equal("{\"id\":4,\"name\":\"B\"}", Flat(records[4]));
    }

    public static TheoryData<IdKind, int, string, string> CommandRows => new()
    {
        { IdKind.Item, 126, "[3,0,0,1]", "[4,0,0,1]" },
        { IdKind.Item, 111, "[8,3,false]", "[8,4,false]" },
        { IdKind.Item, 122, "[1,1,0,3,0,3,0]", "[1,1,0,3,0,4,0]" },
        { IdKind.Item, 302, "[0,3,0,0,false]", "[0,4,0,0,false]" },
        { IdKind.Item, 605, "[0,3,0,0]", "[0,4,0,0]" },
        { IdKind.Weapon, 127, "[3,0,0,1,false]", "[4,0,0,1,false]" },
        { IdKind.Weapon, 111, "[9,3,false]", "[9,4,false]" },
        { IdKind.Weapon, 111, "[4,1,4,3]", "[4,1,4,4]" },
        { IdKind.Weapon, 122, "[1,1,0,3,1,3,0]", "[1,1,0,3,1,4,0]" },
        { IdKind.Weapon, 302, "[1,3,0,0,false]", "[1,4,0,0,false]" },
        { IdKind.Weapon, 605, "[1,3,0,0]", "[1,4,0,0]" },
        { IdKind.Weapon, 319, "[1,1,3]", "[1,1,4]" },
        { IdKind.Armor, 128, "[3,0,0,1,false]", "[4,0,0,1,false]" },
        { IdKind.Armor, 111, "[10,3,false]", "[10,4,false]" },
        { IdKind.Armor, 111, "[4,1,5,3]", "[4,1,5,4]" },
        { IdKind.Armor, 122, "[1,1,0,3,2,3,0]", "[1,1,0,3,2,4,0]" },
        { IdKind.Armor, 302, "[2,3,0,0,false]", "[2,4,0,0,false]" },
        { IdKind.Armor, 605, "[2,3,0,0]", "[2,4,0,0]" },
        { IdKind.Armor, 319, "[1,2,3]", "[1,2,4]" },
        { IdKind.Armor, 319, "[1,3,3]", "[1,3,4]" },
        { IdKind.Skill, 318, "[0,1,0,3]", "[0,1,0,4]" },
        { IdKind.Skill, 339, "[0,1,3,-1]", "[0,1,4,-1]" },
        { IdKind.Skill, 111, "[4,1,3,3]", "[4,1,3,4]" },
        { IdKind.State, 313, "[0,1,0,3]", "[0,1,0,4]" },
        { IdKind.State, 333, "[0,0,3]", "[0,0,4]" },
        { IdKind.State, 111, "[4,1,6,3]", "[4,1,6,4]" },
        { IdKind.State, 111, "[5,0,1,3]", "[5,0,1,4]" },
        { IdKind.Enemy, 336, "[0,3,false]", "[0,4,false]" },
        { IdKind.Troop, 301, "[0,3,false,false]", "[0,4,false,false]" },
        { IdKind.CommonEvent, 117, "[3]", "[4]" },
        { IdKind.Actor, 129, "[3,0,true]", "[4,0,true]" },
        { IdKind.Actor, 303, "[3,8]", "[4,8]" },
        { IdKind.Actor, 111, "[4,3,0]", "[4,4,0]" },
        { IdKind.Actor, 122, "[1,1,0,3,3,3,0]", "[1,1,0,3,3,4,0]" },
        { IdKind.Actor, 311, "[0,3,0,0,10,false]", "[0,4,0,0,10,false]" },
        { IdKind.Actor, 312, "[0,3,0,0,10]", "[0,4,0,0,10]" },
        { IdKind.Actor, 313, "[0,3,0,1]", "[0,4,0,1]" },
        { IdKind.Actor, 314, "[0,3]", "[0,4]" },
        { IdKind.Actor, 315, "[0,3,0,0,10,false]", "[0,4,0,0,10,false]" },
        { IdKind.Actor, 316, "[0,3,0,0,1,false]", "[0,4,0,0,1,false]" },
        { IdKind.Actor, 317, "[0,3,0,0,0,1]", "[0,4,0,0,0,1]" },
        { IdKind.Actor, 318, "[0,3,0,1]", "[0,4,0,1]" },
        { IdKind.Actor, 326, "[0,3,0,0,10]", "[0,4,0,0,10]" },
        { IdKind.Actor, 319, "[3,1,1]", "[4,1,1]" },
        { IdKind.Actor, 320, "[3,\"Ann\"]", "[4,\"Ann\"]" },
        { IdKind.Actor, 321, "[3,1,false]", "[4,1,false]" },
        { IdKind.Actor, 322, "[3,\"\",0,\"\",0,\"\"]", "[4,\"\",0,\"\",0,\"\"]" },
        { IdKind.Actor, 324, "[3,\"Bold\"]", "[4,\"Bold\"]" },
        { IdKind.Actor, 325, "[3,\"Tall\"]", "[4,\"Tall\"]" },
        { IdKind.Actor, 339, "[1,3,1,-1]", "[1,4,1,-1]" },
        { IdKind.Class, 321, "[1,3,false]", "[1,4,false]" },
        { IdKind.Class, 111, "[4,1,2,3]", "[4,1,2,4]" },
        { IdKind.Animation, 212, "[0,3,false]", "[0,4,false]" },
        { IdKind.Animation, 337, "[-1,3,false]", "[-1,4,false]" },
        { IdKind.Tileset, 282, "[3]", "[4]" },

        // Same codes, other forms: the 3 is another kind's id, a variable, an enemy index or a constant.
        { IdKind.Item, 122, "[1,1,0,3,1,3,0]", "[1,1,0,3,1,3,0]" },
        { IdKind.Item, 302, "[1,3,0,0,false]", "[1,3,0,0,false]" },
        { IdKind.Item, 122, "[1,1,0,0,3]", "[1,1,0,0,3]" },
        { IdKind.Armor, 319, "[1,1,3]", "[1,1,3]" },
        { IdKind.Skill, 111, "[4,1,6,3]", "[4,1,6,3]" },
        { IdKind.State, 111, "[4,1,5,3]", "[4,1,5,3]" },
        { IdKind.Actor, 311, "[1,3,0,0,10,false]", "[1,3,0,0,10,false]" },
        { IdKind.Actor, 312, "[1,3,0,0,10]", "[1,3,0,0,10]" },
        { IdKind.Actor, 313, "[1,3,0,1]", "[1,3,0,1]" },
        { IdKind.Actor, 314, "[1,3]", "[1,3]" },
        { IdKind.Actor, 315, "[1,3,0,0,10,false]", "[1,3,0,0,10,false]" },
        { IdKind.Actor, 316, "[1,3,0,0,1,false]", "[1,3,0,0,1,false]" },
        { IdKind.Actor, 317, "[1,3,0,0,0,1]", "[1,3,0,0,0,1]" },
        { IdKind.Actor, 318, "[1,3,0,1]", "[1,3,0,1]" },
        { IdKind.Actor, 326, "[1,3,0,0,10]", "[1,3,0,0,10]" },
        { IdKind.Item, 302, "[2,3,0,0,false]", "[2,3,0,0,false]" },
        { IdKind.Actor, 339, "[0,3,1,-1]", "[0,3,1,-1]" },
        { IdKind.Troop, 301, "[1,3,false,false]", "[1,3,false,false]" },
        { IdKind.Class, 321, "[3,1,false]", "[3,1,false]" }
    };

    [Theory]
    [MemberData(nameof(CommandRows))]
    public void Apply_MovedRecord_RewritesEveryCommandRowOfItsKind(IdKind kind, int code, string before, string after)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(kind, InEvent(Command(code, JArray.Parse(before).ToArray<object>())));

        RemapPlan plan = Plan(game, a, b);
        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, plan.Remaps);

        Assert.Equal([$"b-oak|{kind}||3>4"], plan.NewEntries.Select(Entry));
        ChangeOp edited = Assert.Single(moved.Ops, op => op.Key.Record == "event:1");
        Assert.Equal(after, Flat(edited.Value![0]!["parameters"]));
    }

    public static TheoryData<IdKind, string, string, string, string> FieldRows => new()
    {
        { IdKind.Class, "data/Actors.json", "[1].classId", "3", "4" },
        { IdKind.Weapon, "data/Actors.json", "[1].equips", "[3,3,3,0,0]", "[4,3,3,0,0]" },
        { IdKind.Armor, "data/Actors.json", "[1].equips", "[3,3,3,0,0]", "[3,4,4,0,0]" },
        { IdKind.Skill, "data/Classes.json", "[1].learnings", "[{\"level\":1,\"note\":\"\",\"skillId\":3}]", "[{\"level\":1,\"note\":\"\",\"skillId\":4}]" },
        { IdKind.Animation, "data/Skills.json", "[1].animationId", "3", "4" },
        { IdKind.Animation, "data/Items.json", "[1].animationId", "3", "4" },
        { IdKind.Animation, "data/Weapons.json", "[1].animationId", "3", "4" },
        { IdKind.Skill, "data/Enemies.json", "[1].actions", Action(4, 3), "[{\"conditionParam1\":3,\"conditionParam2\":0,\"conditionType\":4,\"rating\":5,\"skillId\":4}]" },
        { IdKind.State, "data/Enemies.json", "[1].actions", Action(4, 3), "[{\"conditionParam1\":4,\"conditionParam2\":0,\"conditionType\":4,\"rating\":5,\"skillId\":3}]" },
        { IdKind.Item, "data/Enemies.json", "[1].dropItems", Drop(1), Drop(1).Replace("\"dataId\":3", "\"dataId\":4", StringComparison.Ordinal) },
        { IdKind.Weapon, "data/Enemies.json", "[1].dropItems", Drop(2), Drop(2).Replace("\"dataId\":3", "\"dataId\":4", StringComparison.Ordinal) },
        { IdKind.Armor, "data/Enemies.json", "[1].dropItems", Drop(3), Drop(3).Replace("\"dataId\":3", "\"dataId\":4", StringComparison.Ordinal) },
        { IdKind.Enemy, "data/Troops.json", "[1].members", "[{\"enemyId\":3,\"x\":0,\"y\":0,\"hidden\":false}]", "[{\"enemyId\":4,\"x\":0,\"y\":0,\"hidden\":false}]" },
        { IdKind.Actor, "data/Troops.json", "[1].pages", TroopPage(3), TroopPage(4) },
        { IdKind.State, "data/Actors.json", "[1].traits", Trait(14, 3), Trait(14, 4) },
        { IdKind.Skill, "data/Weapons.json", "[1].traits", Trait(43, 3), Trait(43, 4) },
        { IdKind.Skill, "data/Enemies.json", "[1].traits", Trait(35, 3), Trait(35, 4) },
        { IdKind.State, "data/States.json", "[1].traits", Trait(13, 3), Trait(13, 4) },
        { IdKind.State, "data/Items.json", "[1].effects", Effect(21, 3), Effect(21, 4) },
        { IdKind.Skill, "data/Skills.json", "[1].effects", Effect(43, 3), Effect(43, 4) },
        { IdKind.CommonEvent, "data/Items.json", "[1].effects", Effect(44, 3), Effect(44, 4) },
        { IdKind.Tileset, Map, "tilesetId", "3", "4" },
        { IdKind.Troop, Map, "encounterList", "[{\"regionSet\":[],\"troopId\":3,\"weight\":5}]", "[{\"regionSet\":[],\"troopId\":4,\"weight\":5}]" },
        { IdKind.Item, Map, "events[1].pages[0].conditions.itemId", "3", "4" },
        { IdKind.Actor, Map, "events[1].pages[0].conditions.actorId", "3", "4" },
        { IdKind.Actor, SystemFile, "partyMembers", "[1,3]", "[1,4]" },
        { IdKind.Actor, SystemFile, "testBattlers", Battler(3, "[1,1,1,0,0]"), Battler(4, "[1,1,1,0,0]") },
        { IdKind.Weapon, SystemFile, "testBattlers", Battler(1, "[3,3,3,0,0]"), Battler(1, "[4,3,3,0,0]") },
        { IdKind.Armor, SystemFile, "testBattlers", Battler(1, "[3,3,3,0,0]"), Battler(1, "[3,4,4,0,0]") },
        { IdKind.Troop, SystemFile, "testTroopId", "3", "4" },
        { IdKind.State, "data/Weapons.json", "[1].traits", Trait(32, 3), Trait(32, 4) },
        { IdKind.Skill, "data/Classes.json", "[1].traits", Trait(43, 3), Trait(43, 4) },
        { IdKind.Skill, "data/Classes.json", "[1].traits", Trait(44, 3), Trait(44, 4) },
        { IdKind.State, "data/Armors.json", "[1].traits", Trait(14, 3), Trait(14, 4) },
        { IdKind.State, "data/Items.json", "[1].effects", Effect(22, 3), Effect(22, 4) },
        { IdKind.Actor, "data/Items.json", "[1].description", "\"\\\\N[3] sells this\"", "\"\\\\N[4] sells this\"" },

        // Same fields, other forms: another condition, drop kind, trait or effect code.
        { IdKind.State, "data/Enemies.json", "[1].actions", Action(2, 3), Action(2, 3) },
        { IdKind.Item, "data/Enemies.json", "[1].dropItems", Drop(2), Drop(2) },
        { IdKind.State, "data/Armors.json", "[1].traits", Trait(11, 3), Trait(11, 3) },
        { IdKind.State, "data/Skills.json", "[1].effects", Effect(11, 3), Effect(11, 3) }
    };

    private static string Action(int conditionType, int id)
    {
        return $"[{{\"conditionParam1\":{id},\"conditionParam2\":0,\"conditionType\":{conditionType},\"rating\":5,\"skillId\":{id}}}]";
    }

    private static string Drop(int kind)
    {
        return $"[{{\"dataId\":3,\"denominator\":1,\"kind\":{kind}}}]";
    }

    private static string TroopPage(int actorId)
    {
        return $"[{{\"conditions\":{{\"actorId\":{actorId},\"actorValid\":true}},\"list\":[{{\"code\":0,\"indent\":0,\"parameters\":[]}}],\"span\":0}}]";
    }

    private static string Trait(int code, int dataId)
    {
        return $"[{{\"code\":{code},\"dataId\":{dataId},\"value\":1}}]";
    }

    private static string Effect(int code, int dataId)
    {
        return $"[{{\"code\":{code},\"dataId\":{dataId},\"value1\":1,\"value2\":0}}]";
    }

    private static string Battler(int actorId, string equips)
    {
        return $"[{{\"actorId\":{actorId},\"equips\":{equips},\"level\":1}}]";
    }

    [Theory]
    [MemberData(nameof(FieldRows))]
    public void Apply_MovedRecord_RewritesEveryFieldRowOfItsKind(IdKind kind, string file, string path, string value, string expected)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(kind, Set(file, path, value));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal([$"b-oak|{kind}||3>4"], plan.NewEntries.Select(Entry));
        Assert.True(merged.IsDeployable);
        Assert.Equal(expected, Flat(Written(merged, game, file).SelectToken(path)));
    }

    [Fact]
    public void Plan_DropWhoseKindOnlyVanillaHolds_IsAmbiguousAndMovesTheOtherMod()
    {
        var game = new Game(("data/Enemies.json", "[null,{\"id\":1,\"name\":\"One\",\"dropItems\":[{\"dataId\":1,\"denominator\":1,\"kind\":1}]}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, Set("data/Enemies.json", "[1].dropItems[0].dataId", "3"));

        RemapPlan plan = Plan(game, a, b);

        Assert.Equal(["a-tin|Item||3>4"], plan.NewEntries.Select(Entry));
        Assert.True(ComposeRemapped(plan, game, a, b).IsDeployable);
    }

    [Fact]
    public void Apply_DropWhoseKindAndIdBothChange_MovesWithItsMod()
    {
        var game = new Game(("data/Enemies.json", "[null,{\"id\":1,\"name\":\"One\",\"dropItems\":[{\"dataId\":1,\"denominator\":1,\"kind\":2}]}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, files =>
        {
            Set("data/Enemies.json", "[1].dropItems[0].dataId", "3")(files);
            Set("data/Enemies.json", "[1].dropItems[0].kind", "1")(files);
        });

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Item||3>4"], plan.NewEntries.Select(Entry));
        Assert.Equal("{\"dataId\":4,\"denominator\":1,\"kind\":1}", Flat(Written(merged, game, "data/Enemies.json")[1]!["dropItems"]![0]));
    }

    [Fact]
    public void Plan_DropWhoseKindAloneChanges_IsUnknownAndMovesTheOtherMod()
    {
        var game = new Game(("data/Enemies.json", "[null,{\"id\":1,\"name\":\"One\",\"dropItems\":[{\"dataId\":3,\"denominator\":1,\"kind\":2}]}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, Set("data/Enemies.json", "[1].dropItems[0].kind", "1"));

        RemapPlan plan = Plan(game, a, b);

        Assert.Equal(["a-tin|Item||3>4"], plan.NewEntries.Select(Entry));
        Assert.True(ComposeRemapped(plan, game, a, b).IsDeployable);
    }

    [Fact]
    public void Plan_DropWhoseKindAloneChangesToNoId_MovesTheLaterMod()
    {
        var game = new Game(("data/Enemies.json", "[null,{\"id\":1,\"name\":\"One\",\"dropItems\":[{\"dataId\":3,\"denominator\":1,\"kind\":1}]}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, Set("data/Enemies.json", "[1].dropItems[0].kind", "0"));

        Assert.Equal(["b-oak|Item||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    public static TheoryData<string, string, string> LooseIds => new()
    {
        { "field", "3.0", "a-tin" },
        { "field", "\"3\"", "a-tin" },
        { "command", "3.0", "a-tin" },
        { "command", "\"3\"", "a-tin" },

        // Not the id 3 in any reading the game makes.
        { "field", "3.5", "b-oak" },
        { "field", "\"03\"", "b-oak" },
        { "command", "\"3a\"", "b-oak" }
    };

    [Theory]
    [MemberData(nameof(LooseIds))]
    public void Plan_IdNotStoredAsAnInteger_IsAmbiguousWhenTheGameReadsItAsTheId(string where, string id, string moved)
    {
        var game = new Game();
        Action<Func<string, JToken>> edit = where == "field"
            ? Set("data/Actors.json", "[1].classId", id)
            : InEvent(Command(321, 1, JToken.Parse(id), false));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Class, edit);

        Assert.Equal([$"{moved}|Class||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    public static TheoryData<IdKind, int, string> ByVariableRows => new()
    {
        { IdKind.Troop, 301, "[1,5,false,false]" },
        { IdKind.Actor, 311, "[1,5,0,0,10,false]" },
        { IdKind.Actor, 312, "[1,5,0,0,10]" },
        { IdKind.Actor, 313, "[1,5,0,1]" },
        { IdKind.Actor, 314, "[1,5]" },
        { IdKind.Actor, 315, "[1,5,0,0,10,false]" },
        { IdKind.Actor, 316, "[1,5,0,0,1,false]" },
        { IdKind.Actor, 317, "[1,5,0,0,0,1]" },
        { IdKind.Actor, 318, "[1,5,0,1]" },
        { IdKind.Actor, 326, "[1,5,0,0,10]" }
    };

    /// <summary>The command names its id through variable 5, which a Control Variables sets to <paramref name="constant"/>.</summary>
    private static Action<Func<string, JToken>> ByVariable(int code, string parameters, int constant)
    {
        return files =>
        {
            InEvent(Command(code, JArray.Parse(parameters).ToArray<object>()))(files);
            InEvent(Command(122, 5, 5, 0, 0, constant))(files);
        };
    }

    [Theory]
    [MemberData(nameof(ByVariableRows))]
    public void Plan_ByVariableWithTheOldIdAsAConstant_MovesTheOtherMod(IdKind kind, int code, string parameters)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(kind, ByVariable(code, parameters, 3));

        Assert.Equal([$"a-tin|{kind}||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(ByVariableRows))]
    public void Plan_ByVariableWithOtherConstants_MovesTheLaterMod(IdKind kind, int code, string parameters)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(kind, ByVariable(code, parameters, 7));

        Assert.Equal([$"b-oak|{kind}||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    public static TheoryData<int, int, string> RandomRanges => new()
    {
        { 1, 5, "a-tin" },
        { 3, 3, "a-tin" },
        { 5, 1, "a-tin" },
        { 4, 9, "b-oak" },
        { 0, 2, "b-oak" }
    };

    [Theory]
    [MemberData(nameof(RandomRanges))]
    public void Plan_ByVariableWithARandomRange_RefusesWhenTheRangeHoldsTheOldId(int low, int high, string moved)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Troop, files =>
        {
            InEvent(Command(301, 1, 5, false, false))(files);
            InEvent(Command(122, 5, 5, 0, 2, low, high))(files);
        });

        Assert.Equal([$"{moved}|Troop||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_RandomRangeWithNoByVariableCommand_MovesTheLaterMod()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Troop, InEvent(Command(122, 5, 5, 0, 2, 1, 5)));

        Assert.Equal(["b-oak|Troop||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_RecordedEntryWhoseModNowNamesTheOldIdInAScript_StillAppliesWithAWarning()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, InEvent(Command(355, "$gameParty.gainItem($dataItems[3], 1)")));
        var recorded = new IdRemap("b-oak", IdKind.Item, "", 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([a, b], [recorded], ["a-tin", "b-oak"], game.Bases, null);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Empty(plan.NewEntries);
        Assert.Equal(["b-oak|Item||3>4"], plan.Remaps.Select(Entry));
        OperationDiagnostic warning = Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapStaleReference);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("b-oak", warning.Subject);
        Assert.True(merged.IsDeployable);
        Assert.Equal("B", Written(merged, game, Items)[4]!["name"]!.Value<string>());
    }

    [Fact]
    public void Plan_RecordedEntryWithNoFreeText_GivesNoWarning()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item);
        var recorded = new IdRemap("b-oak", IdKind.Item, "", 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([a, b], [recorded], ["a-tin", "b-oak"], game.Bases, null);

        Assert.Equal(["b-oak|Item||3>4"], plan.Remaps.Select(Entry));
        Assert.Empty(plan.Diagnostics);
    }

    public static TheoryData<string, string> TextCodes => new()
    {
        { "Hello \\N[3]", "Hello \\N[4]" },
        { "Hello \\n[3]", "Hello \\n[4]" },
        { "\\N[3] meets \\N[1]", "\\N[4] meets \\N[1]" },
        { "A slash: \\\\N[3]", "A slash: \\\\N[3]" },
        { "A slash, then a code: \\\\\\N[3]", "A slash, then a code: \\\\\\N[4]" },
        { "Party of 3", "Party of 3" }
    };

    [Theory]
    [MemberData(nameof(TextCodes))]
    public void Apply_MovedActor_RewritesTheNameCodeInMessageText(string before, string after)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Actor, InEvent(Command(401, before)));

        RemapPlan plan = Plan(game, a, b);
        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, plan.Remaps);

        Assert.Equal(["b-oak|Actor||3>4"], plan.NewEntries.Select(Entry));
        Assert.Equal(after, moved.Ops.Single(op => op.Key.Record == "event:1").Value![0]!["parameters"]![0]!.Value<string>());
    }

    [Fact]
    public void Plan_Target_SitsAboveFillerOfAnotherMod()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item);
        RemapMod filler = game.Mod("c-elm", 2, file =>
        {
            var items = (JArray)file(Items);
            AddRecord(items, 3, "");
            AddRecord(items, 4, "");
            AddRecord(items, 5, "");
        });

        RemapPlan plan = Plan(game, a, b, filler);
        MergePlan merged = ComposeRemapped(plan, game, a, b, filler);

        Assert.Equal(["b-oak|Item||3>6"], plan.NewEntries.Select(Entry));
        Assert.Empty(merged.Collisions);
        JToken items = Written(merged, game, Items);
        Assert.Equal("A", items[3]!["name"]!.Value<string>());
        Assert.Equal("B", items[6]!["name"]!.Value<string>());
    }

    public static TheoryData<string> OwnUses => new() { "303", "322", "text code", "field" };

    private static Action<Func<string, JToken>> OwnUse(string use)
    {
        return use switch
        {
            "303" => InEvent(Command(303, 3, 8)),
            "322" => InEvent(Command(322, 3, "Hero", 0, "", 0, "")),
            "text code" => InEvent(Command(401, "\\N[3] wakes up")),
            _ => Set(SystemFile, "partyMembers", "[1,3]")
        };
    }

    [Theory]
    [MemberData(nameof(OwnUses))]
    public void Plan_BlankActorItsOwnModNames_IsRealAndMoves(string use)
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, file => AddRecord(file("data/Actors.json"), 3, "A"));
        RemapMod b = game.Mod("b-oak", 1, file =>
        {
            AddRecord(file("data/Actors.json"), 3, "");
            OwnUse(use)(file);
        });

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Actor||3>4"], plan.NewEntries.Select(Entry));
        Assert.True(merged.IsDeployable);
        JToken actors = Written(merged, game, "data/Actors.json");
        Assert.Equal("A", actors[3]!["name"]!.Value<string>());
        Assert.Equal("", actors[4]!["name"]!.Value<string>());
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 1)]
    public void Plan_BlankActorItsOwnModPicksByVariable_IsRealAndStays(int operand, int value)
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, file => AddRecord(file("data/Actors.json"), 3, "A"));
        RemapMod b = game.Mod("b-oak", 1, file =>
        {
            AddRecord(file("data/Actors.json"), 3, "");
            InEvent(Command(311, 1, 5, 0, 0, 10, false))(file);
            InEvent(Command(122, 5, 5, 0, operand, value, 5))(file);
        });

        // Real, and the variable may hold 3, so b-oak cannot move: a-tin does.
        Assert.Equal(["a-tin|Actor||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_BlankActorItsOwnModNeverNames_IsFillerAndYields()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, file => AddRecord(file("data/Actors.json"), 3, "A"));
        RemapMod b = game.Mod("b-oak", 1, file => AddRecord(file("data/Actors.json"), 3, ""));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Empty(plan.NewEntries);
        Assert.Empty(merged.Collisions);
        Assert.Equal("A", Written(merged, game, "data/Actors.json")[3]!["name"]!.Value<string>());
    }

    public static TheoryData<int, string, string> MessageCodes => new()
    {
        { 401, "Take \\II[3]", "a-tin" },
        { 401, "Take \\ii[3]", "a-tin" },
        { 405, "Scroll \\Gold[3]", "a-tin" },

        // The engines' own codes hold no item id, and comments are not read.
        { 401, "Colored \\C[3]", "b-oak" },
        { 401, "Icon \\I[3]", "b-oak" },
        { 401, "Moved \\PX[3]", "b-oak" },
        { 401, "Sized \\FS[3]", "b-oak" },
        { 401, "Count \\V[3]", "b-oak" },
        { 401, "Member \\P[3]", "b-oak" },
        { 401, "Escaped \\\\II[3]", "b-oak" },
        { 108, "Item 3 is \\II[3]", "b-oak" }
    };

    [Theory]
    [MemberData(nameof(MessageCodes))]
    public void Plan_OldIdInATextCode_RefusesOnlyForAPluginCode(int code, string text, string moved)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, InEvent(Command(code, text)));

        Assert.Equal([$"{moved}|Item||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    private static void AddEvent(JToken map, int id, string name)
    {
        var events = (JArray)map["events"]!;
        var added = (JObject)events[2]!.DeepClone();
        added["id"] = id;
        added["name"] = name;
        events.Add(added);
    }

    [Theory]
    [InlineData(RpgMakerEngine.MZ, "b-oak")]
    [InlineData(RpgMakerEngine.MV, "a-tin")]
    [InlineData(null, "a-tin")]
    public void Plan_LocationInfoByCharacter_NamesAnEventOnlyOnMz(RpgMakerEngine? engine, string moved)
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, file => AddEvent(file(Map), 3, "A"));
        RemapMod b = game.Mod("b-oak", 1, file =>
        {
            AddEvent(file(Map), 3, "B");
            InEvent(Command(285, 5, 1, 2, 3, 0))(file);
        });

        Assert.Equal([$"{moved}|MapEvent|data/map001.json|3>4"], Plan(engine, game, a, b).NewEntries.Select(Entry));
    }

    private const string DualWieldActors =
        "[null,{\"id\":1,\"name\":\"One\",\"traits\":[{\"code\":55,\"dataId\":1,\"value\":1}]},{\"id\":2,\"name\":\"Two\"}]";

    public static TheoryData<string> SecondSlots => new() { "command", "field", "test battler" };

    private static Action<Func<string, JToken>> SecondSlot(string where)
    {
        return where switch
        {
            "command" => InEvent(Command(319, 1, 2, 3)),
            "field" => Set("data/Actors.json", "[1].equips", "[1,3,0,0,0]"),
            _ => Set(SystemFile, "testBattlers", Battler(1, "[1,3,0,0,0]"))
        };
    }

    [Theory]
    [MemberData(nameof(SecondSlots))]
    public void Plan_SecondSlotInADualWieldGame_IsAmbiguousAndMovesTheOtherMod(string where)
    {
        var game = new Game(("data/Actors.json", DualWieldActors));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Armor, SecondSlot(where));

        Assert.Equal(["a-tin|Armor||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(SecondSlots))]
    public void Plan_SecondSlotWithoutTrait55_MovesTheLaterMod(string where)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Armor, SecondSlot(where));

        Assert.Equal(["b-oak|Armor||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(SecondSlots))]
    public void Plan_SecondSlotWeaponInADualWieldGame_IsAmbiguousAndMovesTheOtherMod(string where)
    {
        var game = new Game(("data/Actors.json", DualWieldActors));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Weapon, SecondSlot(where));

        Assert.Equal(["a-tin|Weapon||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(SecondSlots))]
    public void Plan_SecondSlotWeaponWithoutTrait55_IsArmorAndMovesTheLaterMod(string where)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Weapon, SecondSlot(where));

        Assert.Equal(["b-oak|Weapon||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    /// <summary>Without its file the game may hold trait 55 anywhere; a clash of the other equip kind shows it.</summary>
    [Theory]
    [InlineData("data/Actors.json", IdKind.Armor)]
    [InlineData("data/Classes.json", IdKind.Armor)]
    [InlineData("data/Weapons.json", IdKind.Armor)]
    [InlineData("data/Armors.json", IdKind.Weapon)]
    [InlineData("data/States.json", IdKind.Armor)]
    public void Plan_MissingTraitBase_CountsAsDualWield(string file, IdKind kind)
    {
        var game = new Game((file, null));
        (RemapMod a, RemapMod b) = game.Clash(kind, SecondSlot("command"));

        Assert.Equal([$"a-tin|{kind}||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [InlineData("data/Actors.json")]
    [InlineData("data/Classes.json")]
    [InlineData("data/Weapons.json")]
    [InlineData("data/Armors.json")]
    [InlineData("data/States.json")]
    public void Plan_Trait55FromAnEnabledMod_CountsAsDualWield(string file)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(
            IdKind.Armor,
            files =>
            {
                SecondSlot("command")(files);
                Set(file, "[1].traits", Trait(55, 1))(files);
            });

        Assert.Equal(["a-tin|Armor||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_Trait55FromADisabledMod_IsIgnored()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Armor, SecondSlot("command"));
        RemapMod off = game.Mod("c-elm", 2, Set("data/Classes.json", "[1].traits", Trait(55, 1))) with { Enabled = false };

        Assert.Equal(["b-oak|Armor||3>4"], Plan(game, a, b, off).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_TraitCodePastTheLongRange_IsNoDualWieldAndDoesNotThrow()
    {
        var game = new Game(("data/Classes.json",
            "[null,{\"id\":1,\"name\":\"One\",\"traits\":[{\"code\":99999999999999999999,\"dataId\":99999999999999999999,\"value\":1}]},{\"id\":2,\"name\":\"Two\"}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Armor, SecondSlot("command"));

        Assert.Equal(["b-oak|Armor||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    private static string Records(int count)
    {
        var records = new JArray(JValue.CreateNull());
        for (int id = 1; id < count; id++)
        {
            records.Add(new JObject { ["id"] = id, ["name"] = "R" + id });
        }

        return records.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static (Game Game, RemapMod A, RemapMod B) ClashAt(int id)
    {
        var game = new Game((Items, Records(id)));
        RemapMod a = game.Mod("a-tin", 0, file => AddRecord(file(Items), id, "A"));
        RemapMod b = game.Mod("b-oak", 1, file => AddRecord(file(Items), id, "B"));
        return (game, a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RpgMakerEngine.MV)]
    public void Plan_RecordPast999OnMvOrAnUnknownEngine_RefusesTheMove(RpgMakerEngine? engine)
    {
        (Game game, RemapMod a, RemapMod b) = ClashAt(999);

        RemapPlan plan = Plan(engine, game, a, b);

        Assert.Empty(plan.Remaps);
        Assert.False(ComposeRemapped(plan, game, a, b).IsDeployable);
    }

    [Fact]
    public void Plan_MzRecordPast999_MovesTo1000()
    {
        (Game game, RemapMod a, RemapMod b) = ClashAt(999);

        Assert.Equal(["b-oak|Item||999>1000"], Plan(RpgMakerEngine.MZ, game, a, b).NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_MzRecordPast9999_RefusesTheMove()
    {
        (Game game, RemapMod a, RemapMod b) = ClashAt(9999);

        Assert.Empty(Plan(RpgMakerEngine.MZ, game, a, b).Remaps);
    }

    public static TheoryData<string> FreeTextPlaces => new() { "note", "formula" };

    private static Action<Func<string, JToken>> FreeText(string where)
    {
        return where == "note"
            ? files => files(Items)[3]!["note"] = "<follow:3>"
            : Set("data/Skills.json", "[1].damage", "{\"formula\":\"a.atk * 3\",\"type\":1}");
    }

    [Fact]
    public void Plan_OldIdInAFormulaLeafOfTheLaterMod_MovesTheEarlierModInstead()
    {
        var game = new Game(("data/Skills.json",
            "[null,{\"id\":1,\"name\":\"One\",\"damage\":{\"formula\":\"0\",\"type\":1}},{\"id\":2,\"name\":\"Two\"}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, Set("data/Skills.json", "[1].damage.formula", "\"a.atk * 3\""));

        Assert.Equal(["a-tin|Item||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(FreeTextPlaces))]
    public void Plan_OldIdInFreeTextOfTheLaterMod_MovesTheEarlierModInstead(string where)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, FreeText(where));

        Assert.Equal(["a-tin|Item||3>4"], Plan(game, a, b).NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(FreeTextPlaces))]
    public void Plan_OldIdInFreeTextOfBothMods_KeepsTheClashBlocking(string where)
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, FreeText(where), FreeText(where));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Empty(plan.Offers);
        Collision clash = Assert.Single(merged.Collisions, item => item.Kind == CollisionKind.IdClash);
        Assert.Equal("id:3", clash.Key.Record);
        Assert.True(clash.Blocking);
        Assert.False(merged.IsDeployable);
    }

    [Fact]
    public void Plan_OldIdOnlyInAShippedPluginFile_OffersRemapAndTheChosenOfferDeploys()
    {
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item, editA: FreeText("note"), scriptsB: "var step = 3;");

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Equal(["b-oak|Item||3>4"], plan.Offers.Select(Entry));
        Collision clash = Assert.Single(merged.Collisions);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.Remap, ResolutionKind.DisableMod], clash.Allowed);
        Assert.False(merged.IsDeployable);

        IdRemap chosen = plan.OfferFor(clash, "b-oak")!;
        RemapPlan next = IdRemapPlanner.Plan([a, b], [chosen], [], game.Bases, null);
        Assert.Equal([chosen], next.Remaps);
        Assert.True(ComposeRemapped(next, game, a, b).IsDeployable);
    }

    [Fact]
    public void Apply_MovedRecord_RewritesAWholeNewMapFile()
    {
        const string newMap = "data/Map002.json";
        var game = new Game();
        (RemapMod a, RemapMod b) = game.Clash(
            IdKind.Item,
            files => ((JArray)files(newMap)["events"]![1]!["pages"]![0]!["list"]!).Insert(0, Command(126, 3, 0, 0, 1)));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Item||3>4"], plan.NewEntries.Select(Entry));
        Assert.Equal("[4,0,0,1]", Flat(Written(merged, game, newMap)["events"]![1]!["pages"]![0]!["list"]![0]!["parameters"]));
    }

    [Fact]
    public void Write_NewDatabaseFileWithHoles_FillsThemWithBlankRecords()
    {
        const string animations = "data/Animations.json";
        var game = new Game((animations, null));
        RemapMod mod = game.Mod("b-oak", 0, file => AddRecord(file(animations), 3, "B"));

        JToken records = Written(ComposeRemapped(Plan(game, mod), game, mod), game, animations);

        Assert.Equal(4, ((JArray)records).Count);
        Assert.Equal(JTokenType.Null, records[0]!.Type);
        Assert.All([1, 2], id =>
        {
            Assert.Equal(id, records[id]!["id"]!.Value<int>());
            Assert.True(RpgDataSchema.HasBlankShape("data/animations.json", records[id]));
        });
        Assert.Equal("B", records[3]!["name"]!.Value<string>());
    }

    [Fact]
    public void Write_NewDatabaseFileWithoutHoles_IsTheModsOwnFile()
    {
        const string animations = "data/Animations.json";
        var game = new Game((animations, null));
        RemapMod mod = game.Mod("b-oak", 0, file =>
        {
            AddRecord(file(animations), 1, "A");
            AddRecord(file(animations), 2, "B");
        });

        MergePlan merged = ComposeRemapped(Plan(game, mod), game, mod);
        MergedFile written = MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(item => string.Equals(item.File, animations, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(MergedFileSource.ModFile, written.Source);
    }

    [Fact]
    public void Plan_RecordedTargetVanillaNowUses_PlansTheRecordAgainWithAWarning()
    {
        var game = new Game((Items, "[null,{\"id\":1,\"name\":\"One\"},{\"id\":2,\"name\":\"Two\"},null,{\"id\":4,\"name\":\"Four\"}]"));
        (RemapMod a, RemapMod b) = game.Clash(IdKind.Item);
        var recorded = new IdRemap("b-oak", IdKind.Item, "", 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([a, b], [recorded], ["a-tin", "b-oak"], game.Bases, null);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Item||3>5"], plan.NewEntries.Select(Entry));
        Assert.Contains(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapTargetTaken);
        Assert.True(merged.IsDeployable);
        JToken items = Written(merged, game, Items);
        Assert.Equal("Four", items[4]!["name"]!.Value<string>());
        Assert.Equal("B", items[5]!["name"]!.Value<string>());
    }

    [Theory]
    [InlineData(IdKind.Switch)]
    [InlineData(IdKind.Variable)]
    public void Apply_SwitchOrVariableEntry_IsIgnored(IdKind kind)
    {
        var game = new Game();
        RemapMod mod = game.Mod("b-oak", 0, file => AddRecord(file(Items), 3, "B"));
        var entry = new IdRemap("b-oak", kind, "", 3, 9);

        RemapPlan plan = IdRemapPlanner.Plan([mod], [entry], [], game.Bases, null);

        Assert.Empty(plan.Remaps);
        Assert.Same(mod.ChangeSet, IdRemapper.Apply(mod.ChangeSet, [entry]));
    }
}
