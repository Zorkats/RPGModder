using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

/// <summary>TST-15, step 3: switches and variables. Content is invented (TST-2).</summary>
public sealed class IdRemapStateTests
{
    private const string CommonEvents = "data/CommonEvents.json";
    private const string Troops = "data/Troops.json";
    private const string Enemies = "data/Enemies.json";

    /// <summary>Vanilla names switches 1 and 2 and variable 1; its map and common event read switch 1.</summary>
    private const string StateSystem =
        "{\"gameTitle\":\"Sample\",\"versionId\":11,\"switches\":[\"\",\"Door\",\"Lever\"],\"variables\":[\"\",\"Gold\"],\"partyMembers\":[1]}";

    private const string VanillaCommonEvents =
        "[null,{\"id\":1,\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}],\"name\":\"Start\",\"switchId\":1,\"trigger\":0}]";

    private const string VanillaTroops =
        "[null,{\"id\":1,\"members\":[],\"name\":\"Pair\",\"pages\":[{\"conditions\":{\"switchId\":1,\"switchValid\":false},\"list\":[],\"span\":0}]}]";

    private const string VanillaEnemies =
        "[null,{\"id\":1,\"actions\":[{\"conditionParam1\":0,\"conditionParam2\":0,\"conditionType\":0,\"rating\":5,\"skillId\":1}],\"name\":\"Blob\",\"note\":\"\"}]";

    private static readonly IdRemap SwitchTen = new("b-oak", IdKind.Switch, "", 10, 11);
    private static readonly IdRemap VariableTen = new("b-oak", IdKind.Variable, "", 10, 11);

    private sealed class Game
    {
        private readonly Dictionary<string, string> _vanilla = new(StringComparer.Ordinal)
        {
            [SystemFile] = StateSystem,
            [Map] = VanillaMap,
            [CommonEvents] = VanillaCommonEvents,
            [Troops] = VanillaTroops,
            [Enemies] = VanillaEnemies
        };

        public Game(params (string Path, string Text)[] overrides)
        {
            foreach ((string path, string text) in overrides)
            {
                _vanilla[path] = text;
            }

            Bases = MergeTestKit.Bases(_vanilla.Select(pair => Base(pair.Key, pair.Value)).ToArray());
            Files = _vanilla.Select(pair => Base(pair.Key, pair.Value)).ToList();
        }

        public BaseFileSet Bases { get; }

        public List<BaseFile> Files { get; }

        public VanillaStateIds Scan(RpgMakerEngine? engine = RpgMakerEngine.MZ)
        {
            return VanillaStateIds.Scan(Files, engine)!;
        }

        /// <summary>A mod whose files are vanilla's with <paramref name="edit"/> applied; it ships the files it touched.</summary>
        public RemapMod Mod(string modId, int priority, Action<Func<string, JToken>> edit, params string[] scripts)
        {
            var files = new Dictionary<string, JToken>(StringComparer.Ordinal);
            edit(path =>
            {
                if (!files.TryGetValue(path, out JToken? root))
                {
                    root = JToken.Parse(_vanilla[path]);
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
    }

    private static JObject Command(int code, params object[] parameters)
    {
        return new JObject { ["code"] = code, ["indent"] = 0, ["parameters"] = new JArray(parameters) };
    }

    private static JObject Command(int code, string parameters)
    {
        return new JObject { ["code"] = code, ["indent"] = 0, ["parameters"] = JArray.Parse(parameters) };
    }

    /// <summary>Puts <paramref name="command"/> first in event <paramref name="eventId"/>'s page.</summary>
    private static Action<Func<string, JToken>> InEvent(JObject command, int eventId = 1)
    {
        return file => ((JArray)file(Map)["events"]![eventId]!["pages"]![0]!["list"]!).Insert(0, command.DeepClone());
    }

    private static Action<Func<string, JToken>> Both(params Action<Func<string, JToken>>[] edits)
    {
        return file =>
        {
            foreach (Action<Func<string, JToken>> edit in edits)
            {
                edit(file);
            }
        };
    }

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

    private static Action<Func<string, JToken>> NameSwitch(int id, string name)
    {
        return Name("switches", id, name);
    }

    private static Action<Func<string, JToken>> NameVariable(int id, string name)
    {
        return Name("variables", id, name);
    }

    private static Action<Func<string, JToken>> Name(string list, int id, string name)
    {
        return files =>
        {
            var names = (JArray)files(SystemFile)[list]!;
            while (names.Count <= id)
            {
                names.Add("");
            }

            names[id] = name;
        };
    }

    private static Action<Func<string, JToken>> UseSwitches(int first, int last, int eventId = 1)
    {
        return InEvent(Command(121, first, last, 0), eventId);
    }

    private static string Key(string path)
    {
        return MergePath.ToKey(path);
    }

    private static string Entry(IdRemap remap)
    {
        return $"{remap.ModId}|{remap.Kind}|{remap.FromId}>{remap.ToId}";
    }

    private static string Flat(JToken? token)
    {
        return token!.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static RemapPlan Plan(Game game, IReadOnlyList<IdRemap> recorded, IReadOnlyCollection<string> deployed, VanillaStateIds? states, params RemapMod[] mods)
    {
        return IdRemapPlanner.Plan(mods, recorded, deployed, game.Bases, RpgMakerEngine.MZ, states);
    }

    private static RemapPlan Plan(Game game, params RemapMod[] mods)
    {
        return Plan(game, [], [], game.Scan(), mods);
    }

    private static MergePlan ComposeRemapped(RemapPlan plan, Game game, params RemapMod[] mods)
    {
        List<ModChangeSet> sets = mods.Select(mod => IdRemapper.Apply(mod.ChangeSet, plan.Remaps)).ToList();
        return Composer.Compose(sets, [], game.Bases, plan);
    }

    private static JArray SwitchNames(MergePlan merged, Game game)
    {
        string text = MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(file => file.File == Key(SystemFile)).Text!;
        return (JArray)JToken.Parse(text)["switches"]!;
    }

    private static JToken FirstCommand(ModChangeSet set, int eventId = 1)
    {
        ChangeOp edited = Assert.Single(set.Ops, op => op.Key.File == Key(Map) && op.Key.Record == "event:" + eventId);
        return edited.Value is JArray list ? list[0]! : edited.Value!["pages"]![0]!["list"]![0]!;
    }

    // ---- Rows ----

    public static TheoryData<IdKind, int, string, string> CommandRows => new()
    {
        { IdKind.Switch, 121, "[10,10,0]", "[11,11,0]" },
        { IdKind.Switch, 111, "[0,10,0]", "[0,11,0]" },
        { IdKind.Variable, 122, "[10,10,0,0,5]", "[11,11,0,0,5]" },
        { IdKind.Variable, 122, "[1,1,0,1,10]", "[1,1,0,1,11]" },
        { IdKind.Variable, 111, "[1,10,0,5,0]", "[1,11,0,5,0]" },
        { IdKind.Variable, 111, "[1,1,1,10,0]", "[1,1,1,11,0]" },
        { IdKind.Variable, 103, "[10,2]", "[11,2]" },
        { IdKind.Variable, 104, "[10,1]", "[11,1]" },
        { IdKind.Variable, 285, "[10,0,0,1,1]", "[11,0,0,1,1]" },
        { IdKind.Variable, 285, "[1,0,1,10,10]", "[1,0,1,11,11]" },
        { IdKind.Variable, 125, "[0,1,10]", "[0,1,11]" },
        { IdKind.Variable, 126, "[1,0,1,10]", "[1,0,1,11]" },
        { IdKind.Variable, 127, "[1,0,1,10,false]", "[1,0,1,11,false]" },
        { IdKind.Variable, 128, "[1,0,1,10,false]", "[1,0,1,11,false]" },
        { IdKind.Variable, 201, "[1,10,10,10,2,0]", "[1,11,11,11,2,0]" },
        { IdKind.Variable, 202, "[0,1,10,10,10]", "[0,1,11,11,11]" },
        { IdKind.Variable, 203, "[0,1,10,10,2]", "[0,1,11,11,2]" },
        { IdKind.Variable, 231, "[1,\"pic\",0,1,10,10,100,100,255,0]", "[1,\"pic\",0,1,11,11,100,100,255,0]" },
        { IdKind.Variable, 232, "[1,0,0,1,10,10,100,100,255,0,60,true]", "[1,0,0,1,11,11,100,100,255,0,60,true]" },
        { IdKind.Variable, 301, "[1,10,false,false]", "[1,11,false,false]" },
        { IdKind.Variable, 311, "[1,10,0,0,5,false]", "[1,11,0,0,5,false]" },
        { IdKind.Variable, 311, "[0,1,0,1,10,false]", "[0,1,0,1,11,false]" },
        { IdKind.Variable, 312, "[0,1,0,1,10]", "[0,1,0,1,11]" },
        { IdKind.Variable, 312, "[1,10,0,0,5]", "[1,11,0,0,5]" },
        { IdKind.Variable, 313, "[1,10,0,1]", "[1,11,0,1]" },
        { IdKind.Variable, 314, "[1,10]", "[1,11]" },
        { IdKind.Variable, 315, "[0,1,0,1,10,false]", "[0,1,0,1,11,false]" },
        { IdKind.Variable, 315, "[1,10,0,0,5,false]", "[1,11,0,0,5,false]" },
        { IdKind.Variable, 316, "[0,1,0,1,10,false]", "[0,1,0,1,11,false]" },
        { IdKind.Variable, 316, "[1,10,0,0,5,false]", "[1,11,0,0,5,false]" },
        { IdKind.Variable, 317, "[0,1,0,0,1,10]", "[0,1,0,0,1,11]" },
        { IdKind.Variable, 317, "[1,10,0,0,0,5]", "[1,11,0,0,0,5]" },
        { IdKind.Variable, 318, "[1,10,0,1]", "[1,11,0,1]" },
        { IdKind.Variable, 326, "[0,1,0,1,10]", "[0,1,0,1,11]" },
        { IdKind.Variable, 326, "[1,10,0,0,5]", "[1,11,0,0,5]" },
        { IdKind.Variable, 331, "[0,0,1,10,false]", "[0,0,1,11,false]" },
        { IdKind.Variable, 332, "[0,0,1,10]", "[0,0,1,11]" },
        { IdKind.Variable, 342, "[0,0,1,10]", "[0,0,1,11]" },
        { IdKind.Variable, 401, "[\"Has \\\\V[10] and \\\\v[10]\"]", "[\"Has \\\\V[11] and \\\\v[11]\"]" },

        // The engine reads a variable for any designation but 0, not only the 1 the editor writes.
        { IdKind.Variable, 111, "[1,1,2,10,0]", "[1,1,2,11,0]" },
        { IdKind.Variable, 125, "[0,2,10]", "[0,2,11]" },
        { IdKind.Variable, 126, "[1,0,-1,10]", "[1,0,-1,11]" },
        { IdKind.Variable, 201, "[2,10,10,10,2,0]", "[2,11,11,11,2,0]" },
        { IdKind.Variable, 202, "[0,2,10,10,10]", "[0,2,11,11,11]" },
        { IdKind.Variable, 231, "[1,\"pic\",0,2,10,10,100,100,255,0]", "[1,\"pic\",0,2,11,11,100,100,255,0]" },
        { IdKind.Variable, 232, "[1,0,0,-1,10,10,100,100,255,0,60,true]", "[1,0,0,-1,11,11,100,100,255,0,60,true]" },
        { IdKind.Variable, 311, "[2,10,0,0,5,false]", "[2,11,0,0,5,false]" },
        { IdKind.Variable, 317, "[0,1,0,0,2,10]", "[0,1,0,0,2,11]" },
        { IdKind.Variable, 331, "[0,0,-1,10,false]", "[0,0,-1,11,false]" },

        // Same codes, other forms: the 10 is a constant, another kind's id, or read so only by MV.
        { IdKind.Switch, 111, "[1,10,0,0,0]", "[1,10,0,0,0]" },
        { IdKind.Switch, 111, "[2,\"A\",0]", "[2,\"A\",0]" },
        { IdKind.Variable, 111, "[0,10,0]", "[0,10,0]" },
        { IdKind.Variable, 111, "[1,1,0,10,0]", "[1,1,0,10,0]" },
        { IdKind.Variable, 122, "[1,1,0,0,10]", "[1,1,0,0,10]" },
        { IdKind.Variable, 122, "[1,1,0,2,10,20]", "[1,1,0,2,10,20]" },
        { IdKind.Variable, 122, "[1,1,0,3,0,10,0]", "[1,1,0,3,0,10,0]" },
        { IdKind.Variable, 122, "[9,12,0,0,5]", "[9,12,0,0,5]" },
        { IdKind.Variable, 127, "[1,0,0,10,false]", "[1,0,0,10,false]" },
        { IdKind.Variable, 128, "[1,0,0,10,false]", "[1,0,0,10,false]" },
        { IdKind.Variable, 232, "[1,0,0,0,10,10,100,100,255,0,60,true]", "[1,0,0,0,10,10,100,100,255,0,60,true]" },
        { IdKind.Variable, 125, "[0,0,10]", "[0,0,10]" },
        { IdKind.Variable, 126, "[1,0,0,10]", "[1,0,0,10]" },
        { IdKind.Variable, 201, "[0,10,10,10,2,0]", "[0,10,10,10,2,0]" },
        { IdKind.Variable, 202, "[0,0,10,10,10]", "[0,0,10,10,10]" },
        { IdKind.Variable, 203, "[0,0,10,10,2]", "[0,0,10,10,2]" },
        { IdKind.Variable, 231, "[1,\"pic\",0,0,10,10,100,100,255,0]", "[1,\"pic\",0,0,10,10,100,100,255,0]" },
        { IdKind.Variable, 285, "[1,0,0,10,10]", "[1,0,0,10,10]" },
        { IdKind.Variable, 285, "[1,0,2,10,10]", "[1,0,2,10,10]" },
        { IdKind.Variable, 285, "[1,0,3,10,10]", "[1,0,3,10,10]" },
        { IdKind.Variable, 203, "[0,2,10,10,2]", "[0,2,10,10,2]" },
        { IdKind.Variable, 301, "[2,10,false,false]", "[2,10,false,false]" },
        { IdKind.Variable, 301, "[0,10,false,false]", "[0,10,false,false]" },
        { IdKind.Variable, 311, "[0,10,0,0,5,false]", "[0,10,0,0,5,false]" },
        { IdKind.Variable, 311, "[0,1,0,0,10,false]", "[0,1,0,0,10,false]" },
        { IdKind.Variable, 312, "[0,1,0,0,10]", "[0,1,0,0,10]" },
        { IdKind.Variable, 315, "[0,1,0,0,10,false]", "[0,1,0,0,10,false]" },
        { IdKind.Variable, 316, "[0,1,0,0,10,false]", "[0,1,0,0,10,false]" },
        { IdKind.Variable, 317, "[0,1,0,0,0,10]", "[0,1,0,0,0,10]" },
        { IdKind.Variable, 326, "[0,1,0,0,10]", "[0,1,0,0,10]" },
        { IdKind.Variable, 331, "[0,0,0,10,false]", "[0,0,0,10,false]" },
        { IdKind.Variable, 332, "[0,0,0,10]", "[0,0,0,10]" },
        { IdKind.Variable, 342, "[0,0,0,10]", "[0,0,0,10]" }
    };

    [Theory]
    [MemberData(nameof(CommandRows))]
    public void Apply_MovedStateId_RewritesEveryCommandRowOfItsKind(IdKind kind, int code, string before, string after)
    {
        var game = new Game();

        // The anchor in event 2 keeps the entry active when the row under test names no id of the kind.
        JObject anchor = kind == IdKind.Switch ? Command(121, 10, 10, 0) : Command(122, 10, 10, 0, 0, 5);
        RemapMod b = game.Mod("b-oak", 1, Both(InEvent(Command(code, before)), InEvent(anchor, 2)));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [kind == IdKind.Switch ? SwitchTen : VariableTen]);

        Assert.Equal(after, Flat(FirstCommand(moved)["parameters"]));
    }

    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    public void Apply_MovedSwitch_RewritesTheMoveRouteSwitchCommands(int code)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Set(Map, "events[1].pages[0].moveRoute", $"{{\"list\":[{{\"code\":{code},\"parameters\":[10]}},{{\"code\":0}}],\"repeat\":true}}"));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [SwitchTen]);

        ChangeOp edited = Assert.Single(moved.Ops, op => op.Key.Record == "event:1");
        JObject route = ((JContainer)edited.Value!).DescendantsAndSelf().OfType<JObject>().First(item => item["code"]?.Value<int>() == code);
        Assert.Equal($"{{\"code\":{code},\"parameters\":[11]}}", Flat(route));
    }

    public static TheoryData<IdKind, string, string, string, string> FieldRows => new()
    {
        { IdKind.Switch, Map, "events[1].pages[0].conditions.switch1Id", "10", "11" },
        { IdKind.Switch, Map, "events[1].pages[0].conditions.switch2Id", "10", "11" },
        { IdKind.Variable, Map, "events[1].pages[0].conditions.variableId", "10", "11" },
        { IdKind.Switch, Troops, "[1].pages[0].conditions.switchId", "10", "11" },
        { IdKind.Switch, CommonEvents, "[1].switchId", "10", "11" },
        { IdKind.Switch, Enemies, "[1].actions[0].conditionParam1", "10", "11" }
    };

    [Theory]
    [MemberData(nameof(FieldRows))]
    public void Apply_MovedStateId_RewritesEveryFieldRowOfItsKind(IdKind kind, string file, string path, string before, string after)
    {
        var game = new Game();
        Action<Func<string, JToken>> edit = Set(file, path, before);
        if (file == Enemies)
        {
            edit = Both(edit, Set(file, "[1].actions[0].conditionType", "6"));
        }

        RemapMod b = game.Mod("b-oak", 1, edit);

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [kind == IdKind.Switch ? SwitchTen : VariableTen]);

        string leaf = path[(path.LastIndexOf('.') + 1)..];
        List<ChangeOp> ops = moved.Ops.Where(op => op.Key.File == Key(file)).ToList();
        Assert.Contains(ops, op => Flat(op.Value).Contains($"\"{leaf}\":{after}", StringComparison.Ordinal) ||
                                   (op.Path.EndsWith(leaf, StringComparison.Ordinal) && Flat(op.Value) == after));
        Assert.DoesNotContain(ops, op => Flat(op.Value).Contains($"\"{leaf}\":{before}", StringComparison.Ordinal) ||
                                         (op.Path.EndsWith(leaf, StringComparison.Ordinal) && Flat(op.Value) == before));
    }

    [Fact]
    public void Apply_EnemyActionOfAnotherConditionType_KeepsItsParameter()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(
            Set(Enemies, "[1].actions[0].conditionParam1", "10"),
            Set(Enemies, "[1].actions[0].conditionType", "5"),
            UseSwitches(10, 10)));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [SwitchTen]);

        Assert.DoesNotContain(moved.Ops, op => op.Key.File == Key(Enemies) && Flat(op.Value).Contains("\"conditionParam1\":11", StringComparison.Ordinal));
    }

    // ---- Names ----

    [Fact]
    public void Apply_MovedSwitchTheModNames_MovesTheNameWithTheId()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, NameSwitch(10, "Mine"));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [SwitchTen]);

        Assert.Contains(moved.Ops, op => op.Key.File == Key(SystemFile) && op.Path == "switches[11]" && Flat(op.Value) == "\"Mine\"");
        Assert.DoesNotContain(moved.Ops, op => op.Key.File == Key(SystemFile) && op.Path == "switches[10]");
        Assert.Single(moved.Ops, op => op.Path == "switches[11]");
    }

    [Fact]
    public void Apply_MovedSwitchTheModDoesNotName_AddsAnEmptyNameSoTheListReachesIt()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [SwitchTen]);

        ChangeOp slot = Assert.Single(moved.Ops, op => op.Key.File == Key(SystemFile));
        Assert.Equal(("switches[11]", ChangeKind.SetLeaf, "\"\""), (slot.Path, slot.Kind, Flat(slot.Value)));
        Assert.Equal(moved.Ops.OrderBy(op => op, Comparer<ChangeOp>.Create(ChangeSetBuilder.CompareOps)), moved.Ops);
    }

    [Theory]
    [InlineData("")]
    [InlineData("switches")]
    public void Apply_OpHoldingTheWholeNameList_MovesTheNameEmptiesTheOldSlotAndAddsNoSlot(string path)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        JArray names = JArray.Parse("[\"\",\"Door\",\"Lever\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"Mine\"]");
        JToken value = path.Length == 0 ? new JObject { ["switches"] = names, ["variables"] = JArray.Parse("[\"\",\"Gold\"]") } : names;
        ChangeKind kind = path.Length == 0 ? ChangeKind.ReplaceFile : ChangeKind.SetLeaf;
        var whole = new ChangeOp("b-oak", RemapPlaces.StateKey, path, kind, value, null);
        var set = new ModChangeSet("b-oak", 1, b.ChangeSet.Ops.Append(whole).ToList(), []);

        ModChangeSet moved = IdRemapper.Apply(set, [SwitchTen]);

        ChangeOp system = Assert.Single(moved.Ops, op => op.Key == RemapPlaces.StateKey);
        JToken list = path.Length == 0 ? system.Value!["switches"]! : system.Value!;
        Assert.Equal("[\"\",\"Door\",\"Lever\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"Mine\"]", Flat(list));
    }

    [Fact]
    public void Write_MovedSwitch_PadsTheNamesWithEmptyNamesUpToTheNewId()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        var plan = new RemapPlan([SwitchTen], [], [], []);

        MergePlan merged = ComposeRemapped(plan, game, b);
        JToken system = JToken.Parse(MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(file => file.File == Key(SystemFile)).Text!);

        Assert.Equal("[\"\",\"Door\",\"Lever\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\"]", Flat(system["switches"]));
    }

    [Fact]
    public void Compose_EmptyNamePaddingTheList_YieldsToAnotherModsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10)));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(12, "Theirs"), UseSwitches(12, 12, 2)));

        MergePlan merged = ComposeRemapped(Plan(game, a, b), game, a, b);
        JToken system = JToken.Parse(MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(file => file.File == Key(SystemFile)).Text!);

        Assert.Empty(merged.Collisions);
        Assert.Equal("[\"\",\"Door\",\"Lever\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"Mine\",\"\",\"Theirs\"]", Flat(system["switches"]));
    }

    [Fact]
    public void Apply_MovedVariableTheModNames_MovesTheNameWithTheId()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(NameVariable(10, "Mine"), InEvent(Command(122, 10, 10, 0, 0, 5))));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [VariableTen]);

        Assert.Contains(moved.Ops, op => op.Path == "variables[11]" && Flat(op.Value) == "\"Mine\"");
        Assert.DoesNotContain(moved.Ops, op => op.Path == "variables[10]" && Flat(op.Value) == "\"Mine\"");
    }

    [Fact]
    public void Write_MovedVariable_PadsTheNamesWithEmptyNamesUpToTheNewId()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(122, 10, 10, 0, 0, 5)));
        var plan = new RemapPlan([VariableTen], [], [], []);

        MergePlan merged = ComposeRemapped(plan, game, b);
        JToken system = JToken.Parse(MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(file => file.File == Key(SystemFile)).Text!);

        Assert.Equal("[\"\",\"Gold\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\"]", Flat(system["variables"]));
    }

    [Fact]
    public void Compose_EmptyVariableNamePaddingTheList_YieldsToAnotherModsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameVariable(10, "Mine"), InEvent(Command(122, 10, 10, 0, 0, 5))));
        RemapMod b = game.Mod("b-oak", 1, Both(NameVariable(12, "Theirs"), InEvent(Command(122, 12, 12, 0, 0, 5), 2)));

        MergePlan merged = ComposeRemapped(Plan(game, a, b), game, a, b);
        JToken system = JToken.Parse(MergedFileWriter.Write(merged, game.Bases, null, RpgMakerEngine.MZ).Single(file => file.File == Key(SystemFile)).Text!);

        Assert.Empty(merged.Collisions);
        Assert.Equal("[\"\",\"Gold\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"Mine\",\"\",\"Theirs\"]", Flat(system["variables"]));
    }

    [Fact]
    public void Compose_MovedEmptyNameOverTheModsOwnEmptyName_ListsTheModOnce()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), NameSwitch(11, "")));

        MergePlan merged = ComposeRemapped(new RemapPlan([SwitchTen], [], [], []), game, b);

        MergedOp slot = Assert.Single(merged.Ops, item => item.Op.Path == "switches[11]");
        Assert.Equal(["b-oak"], slot.ModIds);
    }

    [Fact]
    public void Compose_EmptyNameOverAVanillaName_StillClashes()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, NameSwitch(2, "Pull"));
        RemapMod b = game.Mod("b-oak", 1, NameSwitch(2, ""));

        Collision clash = Assert.Single(ComposeRemapped(Plan(game, a, b), game, a, b).Collisions);

        Assert.Equal((CollisionKind.ValueClash, "switches[2]"), (clash.Kind, clash.Path));
    }

    [Fact]
    public void Compose_TwoNamesForASharedSwitch_FoldIntoTheOverlapWithTheStayingModsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10)));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(10, 10, 2)));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Collision overlap = Assert.Single(merged.Collisions);
        Assert.Equal(CollisionKind.StateIdOverlap, overlap.Kind);
        Assert.True(merged.IsDeployable);
        Assert.Equal("\"Mine\"", Flat(SwitchNames(merged, game)[10]));

        // Taking the offer carries b-oak's name to its new id.
        RemapPlan moved = Plan(game, plan.OffersFor(overlap, "b-oak"), [], game.Scan(), a, b);
        MergePlan after = ComposeRemapped(moved, game, a, b);
        Assert.Empty(after.Collisions);
        Assert.Equal(("\"Mine\"", "\"Theirs\""), (Flat(SwitchNames(after, game)[10]), Flat(SwitchNames(after, game)[11])));
    }

    [Fact]
    public void Compose_TwoNamesForASharedSwitchNeitherModCanMove_KeepTheLatestName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10), Set(Enemies, "[1].note", "\"10\"")));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(10, 10), Set(Map, "events[2].note", "\"10!\"")));

        MergePlan merged = ComposeRemapped(Plan(game, a, b), game, a, b);

        Assert.Equal(CollisionKind.StateIdOverlap, Assert.Single(merged.Collisions).Kind);
        Assert.Equal("\"Theirs\"", Flat(SwitchNames(merged, game)[10]));
    }

    [Fact]
    public void Compose_SharedSwitchWhoseSlotOneModSetsToNull_StillClashes()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10)));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), files => files(SystemFile)["switches"]![10] = JValue.CreateNull(), UseSwitches(10, 10, 2)));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal("a-tin,b-oak", string.Join(',', Assert.Single(plan.SharedStateIds).ModIds));
        Assert.Equal(
            [(CollisionKind.ValueClash, "switches[10]"), (CollisionKind.StateIdOverlap, "switches[10]")],
            merged.Collisions.Select(item => (item.Kind, item.Path)).Order());
    }

    [Fact]
    public void Compose_SharedSwitchWhoseMoverIsLatest_KeepsTheLatestStayingModsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10), Set(Enemies, "[1].note", "\"10\"")));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(10, 10), Set(Map, "events[2].note", "\"10!\"")));
        RemapMod c = game.Mod("c-elm", 2, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10, 2)));

        RemapPlan plan = Plan(game, a, b, c);
        MergePlan merged = ComposeRemapped(plan, game, a, b, c);

        // c-elm gives a-tin's name but takes its offer, so b-oak is the latest mod that stays.
        Assert.Equal("c-elm", Assert.Single(plan.Offers).ModId);
        Assert.Equal(CollisionKind.StateIdOverlap, Assert.Single(merged.Collisions).Kind);
        Assert.Equal("\"Theirs\"", Flat(SwitchNames(merged, game)[10]));
    }

    [Fact]
    public void Compose_SharedSwitchTwoModsOfOnePriorityKeep_TheLaterIdsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(10, 10), Set(Enemies, "[1].note", "\"10\"")));
        RemapMod b = game.Mod("a-tin", 1, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10), Set(Map, "events[2].note", "\"10!\"")));

        MergePlan merged = ComposeRemapped(Plan(game, a, b), game, a, b);

        Assert.Equal("\"Theirs\"", Flat(SwitchNames(merged, game)[10]));
        Assert.Equal("\"Theirs\"", Flat(SwitchNames(ComposeRemapped(Plan(game, b, a), game, b, a), game)[10]));
    }

    [Theory]
    [InlineData(121)]
    [InlineData(122)]
    public void Compose_SharedSwitchNeitherModCanMoveWithAnOfferElsewhere_KeepsTheLatestName(int code)
    {
        var game = new Game();
        JObject other = code == 121 ? Command(121, 12, 12, 0) : Command(122, 10, 10, 0, 0, 1);
        // Vanilla's switch 2 holds both ranges where they are.
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(2, 10), InEvent(other)));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(2, 10, 2), InEvent(other, 2)));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        // b-oak is offered a move of the other id, switch 12 or variable 10, not of switch 10.
        IdRemap offer = Assert.Single(plan.Offers);
        Assert.Equal(("b-oak", code == 121 ? IdKind.Switch : IdKind.Variable), (offer.ModId, offer.Kind));
        Assert.Equal("\"Theirs\"", Flat(SwitchNames(merged, game)[10]));
    }

    [Theory]
    [InlineData("switches", 2)]
    [InlineData("variables", 10)]
    public void Compose_TwoNamesBesideASharedSwitch_StillClashWhereNothingIsShared(string list, int index)
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(Name(list, index, "Mine"), UseSwitches(10, 10)));
        RemapMod b = game.Mod("b-oak", 1, Both(Name(list, index, "Theirs"), UseSwitches(10, 10)));
        var vanilla = new VanillaStateIds([1, 2], [1, 10], 3, 2);

        MergePlan merged = ComposeRemapped(Plan(game, [], [], vanilla, a, b), game, a, b);

        Assert.Equal(
            [(CollisionKind.ValueClash, $"{list}[{index}]"), (CollisionKind.StateIdOverlap, "switches[10]")],
            merged.Collisions.Select(item => (item.Kind, item.Path)).Order());
    }

    [Fact]
    public void Compose_TwoNamesWithoutAVanillaScan_StillClash()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(NameSwitch(10, "Mine"), UseSwitches(10, 10)));
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Theirs"), UseSwitches(10, 10)));

        Collision clash = Assert.Single(ComposeRemapped(Plan(game, [], [], null, a, b), game, a, b).Collisions);

        Assert.Equal((CollisionKind.ValueClash, true), (clash.Kind, clash.Blocking));
    }

    [Fact]
    public void Apply_InactiveStateEntry_ChangesNothing()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(12, 12));

        Assert.Same(b.ChangeSet.Ops, IdRemapper.Apply(b.ChangeSet, [SwitchTen]).Ops);
        Assert.Same(b.ChangeSet.Ops, IdRemapper.Apply(b.ChangeSet, [SwitchTen with { Scope = "data/Map001.json" }]).Ops);
    }

    [Fact]
    public void Apply_StateEntryNamedOnlyInFreeText_ChangesNothing()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Set(Enemies, "[1].note", "\"<Flag:10>\""));

        Assert.Same(b.ChangeSet.Ops, IdRemapper.Apply(b.ChangeSet, [SwitchTen]).Ops);
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(10, 5001)]
    [InlineData(10, 0)]
    public void Apply_DamagedStateEntry_ChangesNothing(int fromId, int toId)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(InEvent(Command(111, 0, 0, 0)), UseSwitches(10, 10, 2)));

        Assert.Same(b.ChangeSet.Ops, IdRemapper.Apply(b.ChangeSet, [new IdRemap("b-oak", IdKind.Switch, "", fromId, toId)]).Ops);
    }

    [Fact]
    public void Apply_EntryNamedOnlyInAnMvLocationRow_StaysInForce()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(285, 1, 0, 2, 10, 10)));

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, [VariableTen]);

        Assert.Contains(moved.Ops, op => op.Key == RemapPlaces.StateKey && op.Path == "variables[11]");
    }

    // ---- Ranges (GR-R14) ----

    [Theory]
    [InlineData("[9,12,0]", "9>13,10>14,11>15,12>16", "[13,16,0]")]
    [InlineData("[9,12,0]", "10>11", "[9,12,0]")]
    [InlineData("[9,10,0]", "9>13,10>15", "[9,10,0]")]
    [InlineData("[0,1,0]", "1>13", "[13,13,0]")]
    [InlineData("[12,9,0]", "9>13,10>14,11>15,12>16", "[12,9,0]")]
    public void Apply_Range_MovesOnlyWhenEveryIdMovesOntoConsecutiveIds(string before, string entries, string after)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(InEvent(Command(121, before)), NameSwitch(9, "N9"), NameSwitch(10, "N10"), NameSwitch(1, "Door")));
        List<IdRemap> remaps = entries.Split(',')
            .Select(item => item.Split('>'))
            .Select(pair => new IdRemap("b-oak", IdKind.Switch, "", int.Parse(pair[0]), int.Parse(pair[1])))
            .ToList();

        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, remaps);

        Assert.Equal(after, Flat(FirstCommand(moved)["parameters"]));
    }

    // ---- Scan ----

    [Fact]
    public void Scan_References_CountRangesNamesAndRowsButNotFreeText()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(
            InEvent(Command(121, 20, 22, 0)),
            InEvent(Command(122, 7, 7, 0, 0, 1), 2),
            NameSwitch(30, "Lamp"),
            NameSwitch(31, ""),
            Set(CommonEvents, "[1].name", "\"Uses 40\"")));

        ModReferences references = ReferenceMap.Scan(b.ChangeSet, [], dualWield: false, mz: true);

        Assert.Equal([20, 21, 22, 30], references.StateIds(IdKind.Switch, 5000));
        Assert.Equal([7], references.StateIds(IdKind.Variable, 5000));
        Assert.Equal([20, 21], references.StateIds(IdKind.Switch, 21));
        Assert.True(references.Uses(IdKind.Switch, 21));
        Assert.False(references.Uses(IdKind.Switch, 31));
    }

    [Fact]
    public void Scan_RangeWithAnEndThatIsNotAWholeNumber_BlocksEveryIdOfItsKind()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(121, "[\"9\",12,0]")));

        ModReferences references = ReferenceMap.Scan(b.ChangeSet, [], dualWield: false, mz: true);

        Assert.True(references.Blocks(IdKind.Switch, 500));
        Assert.False(references.Blocks(IdKind.Variable, 500));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Scan_LocationInfoByCharacter_IsAVariableOnlyOffMz(bool mz, bool blocks)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(285, 1, 0, 2, 10, 10)));

        ModReferences references = ReferenceMap.Scan(b.ChangeSet, [], dualWield: false, mz);

        Assert.Equal(blocks, references.Blocks(IdKind.Variable, 10));
        Assert.Equal(blocks, references.StateIds(IdKind.Variable, 5000).Contains(10));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Scan_LocationInfoByAnyOtherDesignation_IsAVariableOnlyOffMz(int designation)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(285, 1, 0, designation, 10, 10)));

        Assert.True(ReferenceMap.Scan(b.ChangeSet, [], dualWield: false, mz: false).Blocks(IdKind.Variable, 10));
        Assert.False(ReferenceMap.Scan(b.ChangeSet, [], dualWield: false, mz: true).Blocks(IdKind.Variable, 10));
    }

    [Fact]
    public void ScanVanilla_Game_FindsNamesAndTableReferencesAndCounts()
    {
        var game = new Game(
            (Map, EditMap(map => map["events"]![2]!["pages"]![0]!["list"]!.First!.AddBeforeSelf(Command(122, 4, 5, 0, 0, 1)))),
            (CommonEvents, "[null,{\"id\":1,\"list\":[],\"name\":\"Uses 70 in its name\",\"switchId\":3,\"trigger\":0}]"),
            (PluginList, VanillaPlugins));

        VanillaStateIds vanilla = game.Scan();

        Assert.Equal([1, 2, 3], vanilla.Switches);
        Assert.Equal([1, 4, 5], vanilla.Variables);
        Assert.Equal((3, 2), (vanilla.SwitchCount, vanilla.VariableCount));
        Assert.Equal(vanilla.Switches, vanilla.Of(IdKind.Switch));
        Assert.Empty(vanilla.Of(IdKind.Item));
    }

    [Theory]
    [InlineData(RpgMakerEngine.MZ, "1")]
    [InlineData(RpgMakerEngine.MV, "1,7,8")]
    public void ScanVanilla_LocationInfoByCharacter_CountsOnlyOffMz(RpgMakerEngine engine, string variables)
    {
        var game = new Game((Map, EditMap(map => map["events"]![2]!["pages"]![0]!["list"]!.First!.AddBeforeSelf(Command(285, 1, 0, 2, 7, 8)))));

        Assert.Equal(variables, string.Join(',', game.Scan(engine).Variables));
    }

    [Fact]
    public void ScanVanilla_UnreadableDataFile_ReturnsNull()
    {
        var game = new Game((Troops, "[null,{"));

        Assert.Null(VanillaStateIds.Scan(game.Files, RpgMakerEngine.MZ));
    }

    // ---- Planner and composer ----

    [Fact]
    public void Plan_TwoModsSharingASwitch_OffersTheLaterModAMoveAndTheOverlapDefaultsToAccept()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Equal(["b-oak|Switch|10>11"], plan.Offers.Select(Entry));
        Collision overlap = Assert.Single(merged.Collisions);
        Assert.Equal(CollisionKind.StateIdOverlap, overlap.Kind);
        Assert.Equal((Key(SystemFile), "*", "switches[10]"), (overlap.Key.File, overlap.Key.Record, overlap.Path));
        Assert.Equal(["a-tin", "b-oak"], overlap.ModIds);
        Assert.False(overlap.Blocking);
        Assert.Equal([ResolutionKind.Remap, ResolutionKind.Accept], overlap.Allowed);
        CollisionOutcome outcome = Assert.Single(merged.Outcomes);
        Assert.Equal((ResolutionSource.Default, ResolutionKind.Accept), (outcome.Source, outcome.Kind));
        Assert.True(merged.IsDeployable);
        Assert.DoesNotContain(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapUnchecked);
        Assert.Equal(["b-oak|Switch|10>11"], plan.OffersFor(overlap, "b-oak").Select(Entry));
        Assert.Empty(plan.OffersFor(overlap, "a-tin"));
    }

    [Fact]
    public void Plan_SharedVariable_IsAnOverlapOnItsName()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, InEvent(Command(122, 10, 10, 0, 0, 1)));
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(103, 10, 2), 2));

        RemapPlan plan = Plan(game, a, b);
        Collision overlap = Assert.Single(ComposeRemapped(plan, game, a, b).Collisions);

        Assert.Equal("variables[10]", overlap.Path);
        Assert.Equal(["b-oak|Variable|10>11"], plan.OffersFor(overlap, "b-oak").Select(Entry));
    }

    [Fact]
    public void Plan_OverlapStoredAsAccepted_KeepsTheChoiceAcrossModUpdates()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        RemapMod newerB = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), InEvent(Command(121, 10, 10, 1), 2)));

        RemapPlan plan = Plan(game, a, b);
        Collision overlap = Assert.Single(ComposeRemapped(plan, game, a, b).Collisions);
        RemapPlan newer = Plan(game, a, newerB);
        Collision again = Assert.Single(ComposeRemapped(newer, game, a, newerB).Collisions);

        Assert.Equal(overlap.Id, again.Id);
        Assert.Equal(overlap.Fingerprint, again.Fingerprint);
        List<ModChangeSet> sets = [a.ChangeSet, newerB.ChangeSet];
        MergePlan stored = Composer.Compose(sets, [new Resolution(overlap.Id, ResolutionKind.Accept, null, overlap.Fingerprint)], game.Bases, newer);
        CollisionOutcome outcome = Assert.Single(stored.Outcomes);
        Assert.Equal((ResolutionSource.Profile, ResolutionKind.Accept), (outcome.Source, outcome.Kind));
    }

    [Fact]
    public void Plan_WithoutAVanillaScan_SharesNothing()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));

        RemapPlan plan = Plan(game, [], [], null, a, b);

        Assert.Empty(plan.Offers);
        Assert.Empty(ComposeRemapped(plan, game, a, b).Collisions);
    }

    [Fact]
    public void Plan_TwoModsUsingAVanillaSwitch_DoNotOverlap()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(1, 2));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(2, 2, 2));

        RemapPlan plan = Plan(game, a, b);

        Assert.Empty(ComposeRemapped(plan, game, a, b).Collisions);
        Assert.Empty(plan.Offers);
    }

    [Fact]
    public void Plan_DeployedMod_StaysAndTheOtherIsOffered()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));

        RemapPlan plan = Plan(game, [], ["b-oak"], game.Scan(), a, b);

        Assert.Equal(["a-tin|Switch|10>11"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_ModThatCannotMove_StaysAndTheOtherIsOffered()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(UseSwitches(10, 10), Set(CommonEvents, "[1].name", "\"Flag 10\"")));
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), Set(Enemies, "[1].note", "\"<Flag:10>\"")));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(10, 10));

        // a-tin names 10 only in a name (no free text there); b-oak names it in a note, so b-oak cannot move.
        RemapPlan plan = Plan(game, a, b, c);

        Assert.Equal(["a-tin|Switch|10>11", "c-elm|Switch|10>12"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_EveryModBlocked_OffersNothingButStillOverlaps()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(UseSwitches(10, 10), Set(Enemies, "[1].note", "\"10\"")));
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), Set(Map, "events[2].note", "\"10!\"")));

        RemapPlan plan = Plan(game, a, b);
        Collision overlap = Assert.Single(ComposeRemapped(plan, game, a, b).Collisions);

        Assert.Empty(plan.Offers);
        Assert.Equal([ResolutionKind.Accept], overlap.Allowed);
    }

    [Fact]
    public void Plan_SharedIdInARange_OffersTheWholeRangeOnConsecutiveIds()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(9, 11, 2), UseSwitches(11, 12, 2)));

        RemapPlan plan = Plan(game, a, b);
        Collision overlap = Assert.Single(ComposeRemapped(plan, game, a, b).Collisions);

        string[] expected = ["b-oak|Switch|9>13", "b-oak|Switch|10>14", "b-oak|Switch|11>15", "b-oak|Switch|12>16"];
        Assert.Equal(expected, plan.Offers.Select(Entry));
        Assert.Equal(expected, plan.OffersFor(overlap, "b-oak").Select(Entry));
    }

    [Fact]
    public void Plan_RangeHoldingAVanillaId_CannotMoveSoTheOtherModIsOffered()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(2, 10));

        RemapPlan plan = Plan(game, a, b);

        // b-oak would move first by priority, but its range holds vanilla's switch 2.
        Assert.Equal(["a-tin|Switch|10>11"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_RangeHoldingAnIdTheModHasAnEntryFor_CannotMoveSoTheOtherModIsOffered()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 11));

        RemapPlan plan = Plan(game, [new IdRemap("b-oak", IdKind.Switch, "", 11, 20)], [], game.Scan(), a, b);

        Assert.Equal("a-tin", Assert.Single(plan.Offers).ModId);
    }

    [Fact]
    public void Plan_RunThatWouldEndPastTheLimit_IsNotOfferedAndTakesNoIds()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 11));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 11));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(20, 20));
        RemapMod d = game.Mod("d-ash", 3, UseSwitches(20, 20));
        var vanilla = new VanillaStateIds([1, 2], [1], 5000, 2);

        RemapPlan plan = Plan(game, [], [], vanilla, a, b, c, d);

        // The floor is 4999: b-oak's pair would end at 5001, so only d-ash's single id fits, at 5000.
        Assert.Equal(["d-ash|Switch|20>5000"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_TwoSeparateSharedIds_EachOverlapListsOnlyItsOwnOffer()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, Both(UseSwitches(10, 10), UseSwitches(20, 20, 2)));
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(20, 20, 2)));

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Collision twenty = Assert.Single(merged.Collisions, item => item.Path == "switches[20]");
        Assert.Equal(["b-oak|Switch|20>22"], plan.OffersFor(twenty, "b-oak").Select(Entry));
    }

    [Fact]
    public void Plan_EarlierModWithAMovableRange_StaysAndTheLaterIsOffered()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(5, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));

        RemapPlan plan = Plan(game, a, b);

        Assert.Equal(["b-oak|Switch|10>11"], plan.Offers.Select(Entry));
        Assert.Single(plan.SharedStateIds);
    }

    [Fact]
    public void Plan_OneModSharingSeveralIdsOfOneRange_GetsOneOffer()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 11));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 12));

        RemapPlan plan = Plan(game, a, b);

        Assert.Equal(["b-oak|Switch|10>13", "b-oak|Switch|11>14", "b-oak|Switch|12>15"], plan.Offers.Select(Entry));
        Assert.Equal(2, plan.SharedStateIds.Count);
    }

    [Fact]
    public void Plan_TargetsPastTheLimit_OfferNothing()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 11));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 11));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(20, 20));
        RemapMod d = game.Mod("d-ash", 3, UseSwitches(20, 20));
        var vanilla = new VanillaStateIds([1, 2], [1], 4999, 2);

        RemapPlan plan = Plan(game, [], [], vanilla, a, b, c, d);

        // The floor is 4998: b-oak's pair takes 4999 and 5000, so d-ash's single id would pass the limit.
        Assert.Equal(["b-oak|Switch|10>4999", "b-oak|Switch|11>5000"], plan.Offers.Select(Entry));
        Assert.Equal(3, plan.SharedStateIds.Count);
    }

    [Fact]
    public void Plan_DisabledModsIds_AreNeverATarget()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        RemapMod off = game.Mod("c-elm", 2, UseSwitches(11, 11)) with { Enabled = false };

        RemapPlan plan = Plan(game, a, b, off);

        Assert.Equal(["b-oak|Switch|10>12"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_DisabledMod_FormsNoOverlap()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod off = game.Mod("b-oak", 1, UseSwitches(10, 10)) with { Enabled = false };

        RemapPlan plan = Plan(game, a, off);

        Assert.Empty(plan.SharedStateIds);
        Assert.Empty(plan.Offers);
        Assert.Empty(ComposeRemapped(plan, game, a).Collisions);
    }

    [Fact]
    public void Plan_PluginNumber_StillOffersAndWarnsOnce()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10), "$gameSwitches.value(10)");

        RemapPlan plan = Plan(game, a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Switch|10>11"], plan.Offers.Select(Entry));
        OperationDiagnostic warning = Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapUnchecked);
        Assert.Equal("b-oak", warning.Subject);
    }

    [Fact]
    public void Plan_InputOrder_DoesNotChangeThePlan()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 12));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(11, 11));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(12, 14));

        RemapPlan forward = Plan(game, a, b, c);
        RemapPlan backward = Plan(game, c, b, a);
        MergePlan forwardMerge = ComposeRemapped(forward, game, a, b, c);
        MergePlan backwardMerge = ComposeRemapped(backward, game, c, b, a);

        Assert.Equal(forward.Offers, backward.Offers);
        Assert.Equal(forward.SharedStateIds.Select(item => (item.Kind, item.Id, string.Join(',', item.ModIds))),
            backward.SharedStateIds.Select(item => (item.Kind, item.Id, string.Join(',', item.ModIds))));
        Assert.Equal(forward.OfferGroups.Select(group => string.Join(',', group.Select(Entry))),
            backward.OfferGroups.Select(group => string.Join(',', group.Select(Entry))));
        Assert.Equal(forward.Diagnostics, backward.Diagnostics);
        Assert.Equal(forwardMerge.Diagnostics, backwardMerge.Diagnostics);
        Assert.Equal(forwardMerge.MergeHash, backwardMerge.MergeHash);
    }

    // ---- Recorded entries ----

    [Fact]
    public void Plan_RecordedStateEntry_AppliesAndEndsTheOverlap()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10, 2));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), a, b);
        MergePlan merged = ComposeRemapped(plan, game, a, b);

        Assert.Equal(["b-oak|Switch|10>11"], plan.Remaps.Select(Entry));
        Assert.Empty(plan.NewEntries);
        Assert.Empty(plan.Offers);
        Assert.Empty(merged.Collisions);
        Assert.Contains(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapApplied);
    }

    [Fact]
    public void Plan_RecordedStateEntryTheModNoLongerNames_IsInactive()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(12, 12));

        Assert.Empty(Plan(game, [SwitchTen], [], game.Scan(), b).Remaps);
    }

    [Fact]
    public void Plan_RecordedStateEntryWhoseTargetVanillaNowUses_IsTakenAndPlannedAgain()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        var vanilla = new VanillaStateIds([1, 2, 11], [1], 3, 2);

        RemapPlan plan = Plan(game, [SwitchTen], [], vanilla, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Contains(plan.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapTargetTaken);
        Assert.Equal(["b-oak|Switch|10>12"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_RecordedStateEntryNamedOnlyInFreeText_IsInactive()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Set(Enemies, "[1].note", "\"<Flag:10>\""));

        Assert.Empty(Plan(game, [SwitchTen], [], game.Scan(), b).Remaps);
    }

    [Theory]
    [InlineData(0, 11)]
    [InlineData(10, 5001)]
    public void Plan_DamagedStateEntry_IsIgnored(int fromId, int toId)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(InEvent(Command(111, 0, 0, 0)), UseSwitches(10, 10, 2)));

        RemapPlan plan = Plan(game, [new IdRemap("b-oak", IdKind.Switch, "", fromId, toId)], [], game.Scan(), b);

        Assert.Empty(plan.Remaps);
        Assert.Empty(plan.Diagnostics);
    }

    [Theory]
    [InlineData(RpgMakerEngine.MV, 1)]
    [InlineData(RpgMakerEngine.MZ, 0)]
    public void Plan_EntryNamedOnlyInALocationRowByCharacter_StaysInForceOnlyOffMz(RpgMakerEngine engine, int remaps)
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, InEvent(Command(285, 1, 0, 2, 10, 10)));

        RemapPlan plan = IdRemapPlanner.Plan([b], [VariableTen], [], game.Bases, engine, game.Scan(engine));

        Assert.Equal(remaps, plan.Remaps.Count);
    }

    [Fact]
    public void Plan_EntryInForceWhoseOldIdVanillaNowUses_StillClaimsItsTarget()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(11, 11, 2));
        var vanilla = new VanillaStateIds([1, 2, 10], [1], 10, 2);

        RemapPlan plan = Plan(game, [SwitchTen], [], vanilla, b, c);

        Assert.Equal(["b-oak|Switch|10>11"], plan.Remaps.Select(Entry));
        SharedStateId shared = Assert.Single(plan.SharedStateIds);
        Assert.Equal((11, "b-oak,c-elm"), (shared.Id, string.Join(',', shared.ModIds)));
        Assert.Equal(["c-elm|Switch|11>12"], plan.Offers.Select(Entry));
    }

    [Fact]
    public void Plan_RangeAnEntryLeavesBehind_StillSharesTheOldId()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(9, 12, 2));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), a, b);

        SharedStateId shared = Assert.Single(plan.SharedStateIds);
        Assert.Equal((10, "a-tin,b-oak"), (shared.Id, string.Join(',', shared.ModIds)));
        Assert.Equal("a-tin", Assert.Single(plan.Offers).ModId);
    }

    [Fact]
    public void Plan_NewIdOnTheModsOwnTarget_MovesAsANewEntry()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 11, 2)));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);
        ModChangeSet moved = IdRemapper.Apply(b.ChangeSet, plan.Remaps);

        Assert.Equal(["b-oak|Switch|10>11", "b-oak|Switch|11>12"], plan.Remaps.Select(Entry));
        Assert.Equal(["b-oak|Switch|11>12"], plan.NewEntries.Select(Entry));
        Assert.Empty(plan.SharedStateIds);
        Assert.Equal(("[11,11,0]", "[12,12,0]"), (Flat(FirstCommand(moved)["parameters"]), Flat(FirstCommand(moved, 2)["parameters"])));
    }

    [Fact]
    public void Plan_NewRangeOnTheModsOwnTarget_MovesWhole()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 12, 2)));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(12, 12));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b, c);

        // Once b-oak's range moves on, it no longer shares 12 with c-elm.
        Assert.Equal(["b-oak|Switch|11>13", "b-oak|Switch|12>14"], plan.NewEntries.Select(Entry));
        Assert.Empty(plan.SharedStateIds);
    }

    [Fact]
    public void Plan_NewIdOnTheModsOwnTargetThatCannotMove_StaysAndWarns()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 11, 2), Set(Enemies, "[1].note", "\"<Flag:11>\"")));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);

        Assert.Empty(plan.NewEntries);
        OperationDiagnostic warning = Assert.Single(plan.Diagnostics);
        Assert.Equal((DiagnosticCodes.RulesRemapStaleReference, "b-oak"), (warning.Code, warning.Subject));
    }

    [Fact]
    public void Plan_NewIdOnTheModsOwnTargetWithNoFreeIdLeft_StaysAndWarns()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(5000, 5000, 2)));

        RemapPlan plan = Plan(game, [SwitchTen with { ToId = 5000 }], [], game.Scan(), b);

        Assert.Empty(plan.NewEntries);
        Assert.Equal((DiagnosticCodes.RulesRemapStaleReference, "b-oak"), (Assert.Single(plan.Diagnostics).Code, plan.Diagnostics[0].Subject));
    }

    [Fact]
    public void Plan_NewRangeHoldingTwoOwnTargets_MovesOnce()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(9, 9), UseSwitches(10, 10, 2), UseSwitches(11, 12)));
        IdRemap[] recorded = [new("b-oak", IdKind.Switch, "", 9, 12), SwitchTen];

        RemapPlan plan = Plan(game, recorded, [], game.Scan(), b);

        Assert.Equal(["b-oak|Switch|11>13", "b-oak|Switch|12>14"], plan.NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_NewEntryOnTheModsOwnTargetOnceRecorded_ChangesNothingMore()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 11, 2)));
        IdRemap[] recorded = [SwitchTen, new("b-oak", IdKind.Switch, "", 11, 12)];

        RemapPlan plan = Plan(game, recorded, ["b-oak"], game.Scan(), b);

        Assert.Equal(["b-oak|Switch|10>11", "b-oak|Switch|11>12"], plan.Remaps.Select(Entry));
        Assert.Empty(plan.NewEntries);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void Plan_NewIdOnTheModsOwnTargetThatAnotherModAlsoUses_StillSharesItWithTheEntry()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 11, 2)));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(11, 11));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b, c);

        // b-oak's new 11 moves on; its entry 10>11 still meets c-elm's 11.
        Assert.Equal(["b-oak|Switch|11>12"], plan.NewEntries.Select(Entry));
        SharedStateId shared = Assert.Single(plan.SharedStateIds);
        Assert.Equal((11, "b-oak,c-elm"), (shared.Id, string.Join(',', shared.ModIds)));
    }

    [Fact]
    public void Compose_NewNameOnTheModsOwnTargetThatCannotMove_KeepsTheNameInTheModsFile()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Old"), NameSwitch(11, "New"), UseSwitches(10, 10), UseSwitches(11, 11, 2), Set(Enemies, "[1].note", "\"<Flag:11>\"")));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);
        MergePlan merged = ComposeRemapped(plan, game, b);

        Assert.Empty(plan.NewEntries);
        Assert.Empty(merged.Collisions);
        Assert.Equal("\"New\"", Flat(SwitchNames(merged, game)[11]));
    }

    [Fact]
    public void Compose_MovedNameOverPaddingInTheModsFile_KeepsTheMovedName()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Old"), NameSwitch(12, "Far"), UseSwitches(10, 10), UseSwitches(12, 12, 2)));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);
        MergePlan merged = ComposeRemapped(plan, game, b);

        Assert.Empty(plan.NewEntries);
        Assert.Empty(merged.Collisions);
        Assert.Equal(("\"Old\"", "\"Far\""), (Flat(SwitchNames(merged, game)[11]), Flat(SwitchNames(merged, game)[12])));
    }

    [Fact]
    public void Compose_NewIdOnTheModsOwnTargetThatAPluginNames_IsOfferedOnAnOverlapOfTheModAlone()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Old"), NameSwitch(11, "New"), UseSwitches(10, 10), UseSwitches(11, 11, 2)), "$gameSwitches.value(11)");

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);
        MergePlan merged = ComposeRemapped(plan, game, b);

        Assert.Empty(plan.NewEntries);
        Assert.Equal(["b-oak|Switch|11>12"], plan.Offers.Select(Entry));
        Assert.Equal(DiagnosticCodes.RulesRemapUnchecked, Assert.Single(plan.Diagnostics).Code);
        Collision overlap = Assert.Single(merged.Collisions);
        Assert.Equal((CollisionKind.StateIdOverlap, "switches[11]", "b-oak"), (overlap.Kind, overlap.Path, string.Join(',', overlap.ModIds)));
        Assert.Contains(ResolutionKind.Remap, overlap.Allowed);
        Assert.Equal("\"New\"", Flat(SwitchNames(merged, game)[11]));

        // Taking the offer moves the new id on, as when no plugin names it.
        RemapPlan moved = Plan(game, [SwitchTen, .. plan.OffersFor(overlap, "b-oak")], [], game.Scan(), b);
        MergePlan after = ComposeRemapped(moved, game, b);
        Assert.Empty(after.Collisions);
        Assert.Equal(("\"Old\"", "\"New\""), (Flat(SwitchNames(after, game)[11]), Flat(SwitchNames(after, game)[12])));
    }

    [Fact]
    public void Compose_NewRangeHoldingTwoOwnTargetsThatAPluginNames_IsOfferedOnceAndWhole()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(9, 9), UseSwitches(10, 10, 2), UseSwitches(11, 12)), "$gameSwitches.value(12)");
        IdRemap[] recorded = [new("b-oak", IdKind.Switch, "", 9, 12), SwitchTen];

        RemapPlan plan = Plan(game, recorded, [], game.Scan(), b);
        Collision overlap = Assert.Single(ComposeRemapped(plan, game, b).Collisions);

        Assert.Empty(plan.NewEntries);
        Assert.Equal((CollisionKind.StateIdOverlap, "switches[11]"), (overlap.Kind, overlap.Path));
        Assert.Equal(["b-oak|Switch|11>13", "b-oak|Switch|12>14"], plan.OffersFor(overlap, "b-oak").Select(Entry));
        Assert.Equal(2, plan.Offers.Count);
    }

    [Fact]
    public void Plan_NewIdOnTheModsOwnTargetThatAPluginAndAnotherModName_IsOfferedOnTheSharedOverlap()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(UseSwitches(10, 10), UseSwitches(11, 11, 2)), "$gameSwitches.value(11)");
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(11, 11));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b, c);

        SharedStateId shared = Assert.Single(plan.SharedStateIds);
        Assert.Equal((11, "b-oak,c-elm"), (shared.Id, string.Join(',', shared.ModIds)));
        Assert.Contains("b-oak|Switch|11>12", plan.Offers.Select(Entry));
        Assert.Empty(plan.NewEntries);
    }

    [Fact]
    public void Compose_NewNameOnTheModsOwnTarget_MovesOnWithItsId()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, Both(NameSwitch(10, "Old"), NameSwitch(11, "New"), UseSwitches(10, 10), UseSwitches(11, 11, 2)));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);
        MergePlan merged = ComposeRemapped(plan, game, b);

        Assert.Empty(merged.Collisions);
        Assert.Equal("[\"\",\"Door\",\"Lever\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"\",\"Old\",\"New\"]", Flat(SwitchNames(merged, game)));
    }

    [Fact]
    public void Plan_RecordedEntryThatSplitsARange_StillAppliesAndWarns()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(9, 12));

        RemapPlan plan = Plan(game, [SwitchTen], [], game.Scan(), b);

        Assert.Equal(["b-oak|Switch|10>11"], plan.Remaps.Select(Entry));
        Assert.Contains(plan.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapStaleReference);
    }

    [Fact]
    public void Plan_RecordedEntriesMovingARangeWhole_DoNotWarn()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(9, 10));
        IdRemap[] recorded = [new("b-oak", IdKind.Switch, "", 9, 20), new("b-oak", IdKind.Switch, "", 10, 21)];

        RemapPlan plan = Plan(game, recorded, [], game.Scan(), b);

        Assert.Equal(2, plan.Remaps.Count);
        Assert.DoesNotContain(plan.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapStaleReference);
    }

    [Fact]
    public void Plan_RecordedEntriesMovingARangeOutOfOrder_Warn()
    {
        var game = new Game();
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(9, 10));
        IdRemap[] recorded = [new("b-oak", IdKind.Switch, "", 9, 20), new("b-oak", IdKind.Switch, "", 10, 22)];

        RemapPlan plan = Plan(game, recorded, [], game.Scan(), b);

        Assert.Equal(2, plan.Remaps.Count);
        Assert.Contains(plan.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapStaleReference);
    }

    [Fact]
    public void Plan_RecordedTargets_AreNeverOfferedAgain()
    {
        var game = new Game();
        RemapMod a = game.Mod("a-tin", 0, UseSwitches(10, 10));
        RemapMod b = game.Mod("b-oak", 1, UseSwitches(10, 10));
        RemapMod c = game.Mod("c-elm", 2, UseSwitches(10, 10));

        RemapPlan plan = Plan(game, [new IdRemap("c-elm", IdKind.Switch, "", 10, 30)], [], game.Scan(), a, b, c);

        Assert.Equal(["b-oak|Switch|10>31"], plan.Offers.Select(Entry));
    }
}
