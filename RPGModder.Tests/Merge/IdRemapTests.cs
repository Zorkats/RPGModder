using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Services;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

/// <summary>TST-15, step 1: map events and maps. Content is invented (TST-2).</summary>
public sealed class IdRemapTests
{
    private const string MapKey = "data/map001.json";
    private const string MapInfos = "data/MapInfos.json";
    private const string CommonEvents = "data/CommonEvents.json";
    private const string NewMap = "data/Map002.json";

    private const string VanillaMapInfos =
        "[null,{\"id\":1,\"expanded\":false,\"name\":\"Field\",\"order\":1,\"parentId\":0,\"scrollX\":0,\"scrollY\":0}]";

    private const string VanillaCommonEvents =
        "[null,{\"id\":1,\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}],\"name\":\"Rest\",\"switchId\":1,\"trigger\":0}]";

    private static readonly BaseFileSet MapBases = Bases(Base(Map, VanillaMap));

    private static JObject Command(int code, params object[] parameters)
    {
        return new JObject { ["code"] = code, ["indent"] = 0, ["parameters"] = new JArray(parameters) };
    }

    private static JObject NewEvent(int id, string name, params JToken[] commands)
    {
        var list = new JArray(commands.Select(command => command.DeepClone()));
        list.Add(Command(0));
        return new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["note"] = "",
            ["x"] = 1,
            ["y"] = 0,
            ["pages"] = new JArray(new JObject
            {
                ["conditions"] = new JObject { ["switch1Id"] = 1, ["switch1Valid"] = false },
                ["trigger"] = 0,
                ["moveRoute"] = new JObject { ["list"] = new JArray(Command(0)), ["repeat"] = true },
                ["list"] = list
            })
        };
    }

    /// <summary>Vanilla's map with <paramref name="added"/> appended to its events and <paramref name="edit"/> applied.</summary>
    private static string MapWith(JObject? added, Action<JObject>? edit = null)
    {
        return EditMap(map =>
        {
            if (added is not null)
            {
                ((JArray)map["events"]!).Add(added);
            }

            edit?.Invoke(map);
        });
    }

    private static JArray FirstList(JObject map, int eventId)
    {
        return (JArray)map["events"]![eventId]!["pages"]![0]!["list"]!;
    }

    private static ModSource Mod(string modId, int priority, params (string Target, string Text)[] files)
    {
        return new ModSource(
            modId,
            priority,
            files.Select(file => new ModFile(file.Target, "files/" + Path.GetFileName(file.Target), file.Text, Hash(file.Text))).ToList(),
            [],
            []);
    }

    private static RemapMod Installed(ModSource mod, BaseFileSet bases, bool enabled = true, params string[] scripts)
    {
        return new RemapMod(ChangeSetBuilder.Build(mod, bases), enabled, scripts);
    }

    private static RemapPlan Plan(BaseFileSet bases, params RemapMod[] mods)
    {
        return Plan(null, bases, mods);
    }

    private static RemapPlan Plan(RpgMakerEngine? engine, BaseFileSet bases, params RemapMod[] mods)
    {
        return IdRemapPlanner.Plan(mods, [], [], bases, engine);
    }

    /// <summary>The order lane A runs: plan, apply per enabled mod, then compose with the offers.</summary>
    private static MergePlan ComposeRemapped(RemapPlan plan, BaseFileSet bases, params RemapMod[] mods)
    {
        List<ModChangeSet> sets = mods
            .Where(mod => mod.Enabled)
            .Select(mod => IdRemapper.Apply(mod.ChangeSet, plan.Remaps))
            .ToList();
        return Composer.Compose(sets, [], bases, plan);
    }

    private static string Entry(IdRemap remap)
    {
        return $"{remap.ModId}|{remap.Kind}|{remap.Scope}|{remap.FromId}>{remap.ToId}";
    }

    private static ModChangeSet ApplyOwn(RemapPlan plan, RemapMod mod)
    {
        return IdRemapper.Apply(mod.ChangeSet, plan.Remaps);
    }

    [Fact]
    public void Plan_TwoModsAddingDifferentEventsAtOneId_MovesTheLaterModAndBothDeploy()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases);

        string before = string.Join('\n', second.ChangeSet.Ops.Select(op => op.Value?.ToString()));

        RemapPlan plan = Plan(MapBases, first, second);
        MergePlan merged = ComposeRemapped(plan, MapBases, first, second);

        Assert.Equal(before, string.Join('\n', second.ChangeSet.Ops.Select(op => op.Value?.ToString())));
        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>4"], plan.NewEntries.Select(Entry));
        Assert.Equal(plan.NewEntries, plan.Remaps);
        Assert.Empty(plan.Offers);
        Assert.True(merged.IsDeployable);
        Assert.Empty(merged.Collisions);
        MergedOp moved = Assert.Single(merged.Ops, item => item.Op.Key.Record == "event:4");
        Assert.Equal(["b-oak"], moved.ModIds);
        Assert.Equal(4, moved.Op.Value!["id"]!.Value<int>());
        Assert.Equal("Guard", merged.Ops.Single(item => item.Op.Key.Record == "event:3").Op.Value!["name"]!.Value<string>());
        OperationDiagnostic applied = Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapApplied);
        Assert.Equal(DiagnosticSeverity.Info, applied.Severity);
        Assert.Equal("b-oak", applied.Subject);
        Assert.Contains("MapEvent 3", applied.Message, StringComparison.Ordinal);
        Assert.Contains("moved to 4", applied.Message, StringComparison.Ordinal);
    }

    public static TheoryData<int, string, string> EventReferenceRows => new()
    {
        { 203, "[3,0,0,0,0]", "[4,0,0,0,0]" },
        { 203, "[1,2,3,0]", "[1,2,4,0]" },
        { 205, "[3,{\"list\":[{\"code\":0}],\"repeat\":false,\"skippable\":false,\"wait\":true}]", "[4,{\"list\":[{\"code\":0}],\"repeat\":false,\"skippable\":false,\"wait\":true}]" },
        { 212, "[3,1,false]", "[4,1,false]" },
        { 213, "[3,1,false]", "[4,1,false]" },
        { 111, "[6,3,2]", "[6,4,2]" },
        { 122, "[1,1,0,3,5,3,0]", "[1,1,0,3,5,4,0]" },
        { 285, "[7,0,2,3,0]", "[7,0,2,4,0]" },

        // Same codes, other forms: the 3 is a switch, a constant, a variable, an actor, a tile or this event.
        { 111, "[0,3,0]", "[0,3,0]" },
        { 122, "[1,1,0,0,3]", "[1,1,0,0,3]" },
        { 122, "[1,1,0,3,3,3,0]", "[1,1,0,3,3,3,0]" },
        { 203, "[1,1,3,0,0]", "[1,1,3,0,0]" },
        { 203, "[0,0,3,0,0]", "[0,0,3,0,0]" },
        { 285, "[7,0,0,3,0]", "[7,0,0,3,0]" }
    };

    [Theory]
    [MemberData(nameof(EventReferenceRows))]
    public void Apply_MovedEvent_RewritesEveryEventRowOfTheReferenceTable(int code, string before, string after)
    {
        JObject reference = Command(code, JArray.Parse(before).ToArray<object>());
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(
            FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant", reference), map => FirstList(map, 1).Insert(0, reference.DeepClone())), 1),
            MapBases);

        RemapPlan plan = Plan(MapBases, first, second);
        ModChangeSet moved = ApplyOwn(plan, second);

        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>4"], plan.NewEntries.Select(Entry));
        ChangeOp added = Assert.Single(moved.Ops, op => op.Kind == ChangeKind.AddRecord);
        Assert.Equal("event:4", added.Key.Record);
        Assert.Equal(after, added.Value!["pages"]![0]!["list"]![0]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
        ChangeOp edited = Assert.Single(moved.Ops, op => op.Key.Record == "event:1");
        Assert.Equal(after, edited.Value![0]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void Apply_MovedMap_RewritesEveryMapRowOfTheReferenceTableAndWritesTheNewFile()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos), Base(SystemFile, VanillaSystem));
        string Infos(string name, int parentOfField) => EditArray(VanillaMapInfos, infos =>
        {
            infos[1]!["parentId"] = parentOfField;
            infos.Add(new JObject { ["id"] = 2, ["expanded"] = false, ["name"] = name, ["order"] = 2, ["parentId"] = 0, ["scrollX"] = 0, ["scrollY"] = 0 });
        });
        string NewMapText(string name, params JToken[] commands) => EditMap(map =>
        {
            map["displayName"] = name;
            map["events"] = new JArray(JValue.CreateNull(), NewEvent(1, "Exit", commands));
        });

        ModSource first = Mod("a-tin", 0, (NewMap, NewMapText("Cave")), (MapInfos, Infos("Cave", 0)));
        ModSource second = Mod(
            "b-oak",
            1,
            (NewMap, NewMapText("Tower", Command(201, 0, 2, 1, 1, 0, 0))),
            (MapInfos, Infos("Tower", 2)),
            (SystemFile, EditSystem(system =>
            {
                system["startMapId"] = 2;
                system["boat"] = new JObject { ["bgm"] = new JObject { ["name"] = "Sea" }, ["startMapId"] = 2 };
                system["ship"] = new JObject { ["startMapId"] = 2 };
                system["airship"] = new JObject { ["startMapId"] = 2 };
            })),
            (Map, MapWith(null, map =>
            {
                FirstList(map, 1).Insert(0, Command(201, 0, 2, 0, 0, 2, 0));
                FirstList(map, 1).Insert(0, Command(202, 1, 0, 2, 3, 3));
                FirstList(map, 1).Insert(0, Command(201, 1, 2, 3, 4, 0, 0));
            })));
        RemapMod a = Installed(first, bases);
        RemapMod b = Installed(second, bases);

        RemapPlan plan = Plan(bases, a, b);
        MergePlan merged = ComposeRemapped(plan, bases, a, b);
        IReadOnlyList<MergedFile> files = MergedFileWriter.Write(merged, bases, null);

        Assert.Equal(["b-oak|Map||2>3"], plan.NewEntries.Select(Entry));
        Assert.True(merged.IsDeployable);
        Assert.Empty(merged.Collisions);

        MergedFile kept = Assert.Single(files, file => file.File == "data/map002.json");
        Assert.Equal(MergedFileSource.ModFile, kept.Source);
        Assert.Equal("a-tin", kept.ModId);

        MergedFile renamed = Assert.Single(files, file => file.File == "data/map003.json");
        Assert.Equal(MergedFileSource.Text, renamed.Source);
        Assert.Equal("data/Map003.json", renamed.DiskPath);
        var tower = JObject.Parse(renamed.Text!);
        Assert.Equal("Tower", tower["displayName"]!.Value<string>());
        Assert.Equal("[0,3,1,1,0,0]", FirstList(tower, 1)[0]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));

        var infos = JArray.Parse(files.Single(file => file.File == "data/mapinfos.json").Text!);
        Assert.Equal("Cave", infos[2]!["name"]!.Value<string>());
        Assert.Equal(3, infos[3]!["id"]!.Value<int>());
        Assert.Equal("Tower", infos[3]!["name"]!.Value<string>());
        Assert.Equal(3, infos[1]!["parentId"]!.Value<int>());

        var system = JObject.Parse(files.Single(file => file.File == "data/system.json").Text!);
        Assert.Equal(3, system["startMapId"]!.Value<int>());
        Assert.Equal(3, system["boat"]!["startMapId"]!.Value<int>());
        Assert.Equal(3, system["ship"]!["startMapId"]!.Value<int>());
        Assert.Equal(3, system["airship"]!["startMapId"]!.Value<int>());

        var field = JObject.Parse(files.Single(file => file.File == "data/map001.json").Text!);
        JArray list = FirstList(field, 1);
        Assert.Equal("[1,2,3,4,0,0]", list[0]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("[1,0,3,3,3]", list[1]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("[0,3,0,0,2,0]", list[2]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void Plan_SecondRunWithTheRecordedEntry_ReusesItAndRecordsNothingNew()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases);
        RemapPlan firstRun = Plan(MapBases, first, second);

        RemapPlan secondRun = IdRemapPlanner.Plan([first, second], firstRun.NewEntries, ["a-tin", "b-oak"], MapBases, null);

        Assert.Empty(secondRun.NewEntries);
        Assert.Equal(firstRun.Remaps, secondRun.Remaps);
        Assert.True(ComposeRemapped(secondRun, MapBases, first, second).IsDeployable);
    }

    [Fact]
    public void Plan_ModInDeployedMods_KeepsItsIdOverProfileOrder()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases);

        RemapPlan plan = IdRemapPlanner.Plan([first, second], [], ["b-oak"], MapBases, null);

        Assert.Equal(["a-tin|MapEvent|data/map001.json|3>4"], plan.NewEntries.Select(Entry));
    }

    public static TheoryData<string> FreeTextHits => new()
    {
        "script",
        "note",
        "formula",
        "plugin parameter",
        "script line",
        "plugin command",
        "plugin command args",
        "route script",
        "branch script",
        "variable script"
    };

    /// <summary>The event command that holds the number 3 in free text, for the <paramref name="where"/> that name one.</summary>
    private static JObject? FreeTextCommand(string? where)
    {
        return where switch
        {
            "script" => Command(355, "$gameMap.event(3).erase();"),
            "script line" => Command(655, "$gameMap.event(3).erase();"),
            "plugin command" => Command(356, "Follow 3"),
            "plugin command args" => Command(357, "Alpha", "follow", "", new JObject { ["target"] = "3" }),
            "branch script" => Command(111, 12, "$gameMap.event(3).x > 0"),
            "variable script" => Command(122, 1, 1, 0, 4, "$gameMap.event(3).x"),
            _ => null
        };
    }

    /// <summary>Mod <paramref name="modId"/> adding event 3, with the number 3 in the free text <paramref name="where"/> names.</summary>
    private static ModSource FreeTextMod(string modId, string name, int priority, string? where)
    {
        JObject? command = FreeTextCommand(where);
        JObject added = NewEvent(3, name, command is null ? [] : [command]);
        if (where == "note")
        {
            added["note"] = "<follow:3>";
        }

        if (where == "route script")
        {
            ((JArray)added["pages"]![0]!["moveRoute"]!["list"]!).Insert(0, Command(45, "this.jump(3, 0);"));
        }

        var files = new List<(string, string)> { (Map, MapWith(added)) };
        if (where == "formula")
        {
            files.Add((Items, EditItems(items => items[1]!["damage"] = new JObject { ["formula"] = "a.atk * 3", ["type"] = 1 })));
        }

        if (where == "plugin parameter")
        {
            files.Add((PluginList, PluginsJs(Plugin("Alpha", "{\"speed\":\"3\",\"Font Size\":\"20\"}"), Plugin("Omega"))));
        }

        return Mod(modId, priority, files.ToArray());
    }

    [Theory]
    [MemberData(nameof(FreeTextHits))]
    public void Plan_OldIdInFreeTextOfTheLaterMod_MovesTheEarlierModInstead(string where)
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(Items, VanillaItems), Base(PluginList, VanillaPlugins));
        RemapMod first = Installed(FreeTextMod("a-tin", "Guard", 0, null), bases);
        RemapMod second = Installed(FreeTextMod("b-oak", "Merchant", 1, where), bases);

        RemapPlan plan = Plan(bases, first, second);

        Assert.Equal(["a-tin|MapEvent|data/map001.json|3>4"], plan.NewEntries.Select(Entry));
    }

    [Theory]
    [MemberData(nameof(FreeTextHits))]
    public void Plan_OldIdInFreeTextOfBothMods_KeepsTheClashBlocking(string where)
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(Items, VanillaItems), Base(PluginList, VanillaPlugins));
        RemapMod first = Installed(FreeTextMod("a-tin", "Guard", 0, where), bases);
        RemapMod second = Installed(FreeTextMod("b-oak", "Merchant", 1, where), bases);

        RemapPlan plan = Plan(bases, first, second);
        MergePlan merged = ComposeRemapped(plan, bases, first, second);

        Assert.Empty(plan.Remaps);
        Assert.Empty(plan.Offers);
        Assert.False(merged.IsDeployable);
        Collision clash = Assert.Single(merged.Collisions, item => item.Kind == CollisionKind.IdClash);
        Assert.Equal("event:3", clash.Key.Record);
        Assert.True(clash.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.DisableMod], clash.Allowed);
    }

    [Fact]
    public void Plan_OldIdOnlyInAShippedPluginFile_OffersRemapWithAWarningAndNeverPicksIt()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(Items, VanillaItems), Base(PluginList, VanillaPlugins));
        RemapMod first = Installed(FreeTextMod("a-tin", "Guard", 0, "script"), bases);
        RemapMod second = Installed(FreeTextMod("b-oak", "Merchant", 1, null), bases, scripts: "var step = 3;");

        RemapPlan plan = Plan(bases, first, second);
        MergePlan merged = ComposeRemapped(plan, bases, first, second);

        Assert.Empty(plan.Remaps);
        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>4"], plan.Offers.Select(Entry));
        Collision clash = Assert.Single(merged.Collisions);
        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.True(clash.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.Remap, ResolutionKind.DisableMod], clash.Allowed);
        Assert.Equal(ResolutionSource.Unresolved, Assert.Single(merged.Outcomes).Source);
        Assert.False(merged.IsDeployable);
        OperationDiagnostic warning = Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapUnchecked);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("b-oak", warning.Subject);
        Assert.Null(plan.OfferFor(clash, "a-tin"));

        // The player chooses Remap: lane A records the offer, and the next plan applies it.
        IdRemap chosen = plan.OfferFor(clash, "b-oak")!;
        RemapPlan next = IdRemapPlanner.Plan([first, second], [chosen], [], bases, null);
        Assert.Empty(next.NewEntries);
        Assert.Equal([chosen], next.Remaps);
        Assert.True(ComposeRemapped(next, bases, first, second).IsDeployable);
    }

    [Fact]
    public void Plan_EntryWhoseOldIdIsNoLongerAdded_StaysInactiveAndChangesNothing()
    {
        RemapMod mod = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 0), MapBases);
        var stale = new IdRemap("b-oak", IdKind.MapEvent, MapKey, 5, 9);

        RemapPlan plan = IdRemapPlanner.Plan([mod], [stale], [], MapBases, null);

        Assert.Empty(plan.Remaps);
        Assert.Empty(plan.NewEntries);
        Assert.Same(mod.ChangeSet, IdRemapper.Apply(mod.ChangeSet, [stale]));
    }

    [Fact]
    public void Plan_Target_SitsAboveDisabledModsAndEveryRecordedEntry()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases);
        RemapMod disabled = Installed(
            FileMod("c-elm", Map, MapWith(null, map =>
            {
                var events = (JArray)map["events"]!;
                events.Add(JValue.CreateNull());
                events.Add(JValue.CreateNull());
                events.Add(JValue.CreateNull());
                events.Add(NewEvent(6, "Sage"));
            })),
            MapBases,
            enabled: false);
        var stale = new IdRemap("c-elm", IdKind.MapEvent, MapKey, 4, 9);

        RemapPlan plan = IdRemapPlanner.Plan([first, second, disabled], [stale], [], MapBases, null);

        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>10"], plan.NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_EqualContentAtOneId_IsNoClash()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod second = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Guard")), 1), MapBases);

        RemapPlan plan = Plan(MapBases, first, second);
        MergePlan merged = ComposeRemapped(plan, MapBases, first, second);

        Assert.Empty(plan.Remaps);
        Assert.True(merged.IsDeployable);
        Assert.Equal(["a-tin", "b-oak"], Assert.Single(merged.Ops).ModIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RpgMakerEngine.MV)]
    [InlineData(RpgMakerEngine.MZ)]
    public void Plan_TargetPastTheEditorLimit_RefusesTheMove(RpgMakerEngine? engine)
    {
        string wide = EditMap(map =>
        {
            var events = (JArray)map["events"]!;
            while (events.Count < 999)
            {
                events.Add(JValue.CreateNull());
            }
        });
        BaseFileSet bases = Bases(Base(Map, wide));
        string Adding(string name) => EditMapText(wide, map => ((JArray)map["events"]!).Add(NewEvent(999, name)));
        RemapMod first = Installed(FileMod("a-tin", Map, Adding("Guard"), 0), bases);
        RemapMod second = Installed(FileMod("b-oak", Map, Adding("Merchant"), 1), bases);

        RemapPlan plan = Plan(engine, bases, first, second);

        Assert.Empty(plan.Remaps);
        Assert.False(ComposeRemapped(plan, bases, first, second).IsDeployable);
    }

    private static string EditMapText(string text, Action<JObject> edit)
    {
        var map = JObject.Parse(text);
        edit(map);
        return map.ToString();
    }

    public static TheoryData<string> MapsByVariable => new()
    {
        "[201,1,5,6,7,0,0]",
        "[202,0,1,5,6,7]"
    };

    [Theory]
    [MemberData(nameof(MapsByVariable))]
    public void Plan_MapByVariableWithTheOldMapIdAsAConstant_RefusesThatModsMove(string transfer)
    {
        int[] parts = JArray.Parse(transfer).Values<int>().ToArray();
        JObject byVariable = Command(parts[0], parts.Skip(1).Cast<object>().ToArray());
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos));
        string NewMapText(string name) => EditMap(map => map["displayName"] = name);
        ModSource first = Mod("a-tin", 0, (NewMap, NewMapText("Cave")));
        ModSource second = Mod(
            "b-oak",
            1,
            (NewMap, NewMapText("Tower")),
            (Map, MapWith(null, map =>
            {
                FirstList(map, 1).Insert(0, byVariable);
                FirstList(map, 1).Insert(0, Command(122, 5, 5, 0, 0, 2));
            })));

        RemapPlan plan = Plan(bases, Installed(first, bases), Installed(second, bases));

        Assert.Equal(["a-tin|Map||2>3"], plan.NewEntries.Select(Entry));
    }

    public static TheoryData<int, string> CommonEventReferences => new()
    {
        { 213, "[3,1,false]" },
        { 285, "[7,0,2,3,0]" }
    };

    [Theory]
    [MemberData(nameof(CommonEventReferences))]
    public void Plan_EventCommandInACommonEvent_IsAmbiguousAndRefusesThatModsMove(int code, string parameters)
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(CommonEvents, VanillaCommonEvents));
        string events = EditArray(VanillaCommonEvents, list => list.Add(new JObject
        {
            ["id"] = 2,
            ["list"] = new JArray(Command(code, JArray.Parse(parameters).ToArray<object>()), Command(0)),
            ["name"] = "Wave",
            ["switchId"] = 1,
            ["trigger"] = 0
        }));
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), bases);
        RemapMod second = Installed(Mod("b-oak", 1, (Map, MapWith(NewEvent(3, "Merchant"))), (CommonEvents, events)), bases);

        RemapPlan plan = Plan(bases, first, second);

        Assert.Equal(["a-tin|MapEvent|data/map001.json|3>4"], plan.NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_MissingVanillaMapInfos_LeavesTheMapClashBlocking()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = Mod("a-tin", 0, (NewMap, EditMap(map => map["displayName"] = "Cave")));
        ModSource second = Mod("b-oak", 1, (NewMap, EditMap(map => map["displayName"] = "Tower")));
        RemapMod a = Installed(first, bases);
        RemapMod b = Installed(second, bases);

        RemapPlan plan = Plan(bases, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Equal(CollisionKind.IdClash, Assert.Single(ComposeRemapped(plan, bases, a, b).Collisions).Kind);
    }

    [Fact]
    public void Plan_ShuffledInputs_GiveTheSamePlan()
    {
        RemapMod[] mods =
        [
            Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases),
            Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases),
            Installed(FileMod("c-elm", Map, MapWith(NewEvent(3, "Sage")), 2), MapBases)
        ];
        IdRemap[] recorded = [new IdRemap("z-old", IdKind.MapEvent, MapKey, 3, 7), new IdRemap("y-old", IdKind.MapEvent, MapKey, 3, 8)];

        RemapPlan forward = IdRemapPlanner.Plan(mods, recorded, ["c-elm"], MapBases, null);
        RemapPlan backward = IdRemapPlanner.Plan(mods.Reverse().ToList(), recorded.Reverse().ToList(), ["c-elm"], MapBases, null);

        Assert.Equal(["a-tin|MapEvent|data/map001.json|3>9", "b-oak|MapEvent|data/map001.json|3>10"], forward.NewEntries.Select(Entry));
        Assert.Equal(forward.Remaps, backward.Remaps);
        Assert.Equal(forward.NewEntries, backward.NewEntries);
    }

    [Fact]
    public void Apply_TwoTargetsForOneOldId_IsACallerBug()
    {
        RemapMod mod = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant"))), MapBases);

        Assert.Throws<ArgumentException>(() => IdRemapper.Apply(
            mod.ChangeSet,
            [new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 4), new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 5)]));
    }

    /// <summary>Vanilla's map with events added at ids 3 and up, in order; a null leaves a hole.</summary>
    private static string MapAdding(params JObject?[] added)
    {
        return MapWith(null, map =>
        {
            var events = (JArray)map["events"]!;
            while (events.Count < 3)
            {
                events.Add(JValue.CreateNull());
            }

            foreach (JObject? item in added)
            {
                events.Add(item is null ? JValue.CreateNull() : item);
            }
        });
    }

    [Fact]
    public void Plan_RecordedMoveOntoAnIdTheModNowAddsThatCannotMove_IsABlockingClashWithoutPickMod()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod updated = Installed(
            FileMod("b-oak", Map, MapAdding(NewEvent(3, "Merchant"), NewEvent(4, "Smith", Command(355, "$gameMap.event(4).erase();"))), 1),
            MapBases);
        var recorded = new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([first, updated], [recorded], ["a-tin", "b-oak"], MapBases, null);
        MergePlan merged = ComposeRemapped(plan, MapBases, first, updated);
        MergePlan picked = Composer.Compose(
            [first.ChangeSet, IdRemapper.Apply(updated.ChangeSet, plan.Remaps)],
            [new Resolution(Assert.Single(merged.Collisions).Id, ResolutionKind.PickMod, "b-oak")],
            MapBases,
            plan);

        Assert.Equal([recorded], plan.Remaps);
        Assert.Empty(plan.NewEntries);
        Collision clash = Assert.Single(merged.Collisions);
        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.Equal("event:4", clash.Key.Record);
        Assert.Equal(["b-oak"], clash.ModIds);
        Assert.Equal([ResolutionKind.DisableMod], clash.Allowed);
        Assert.False(merged.IsDeployable);
        Assert.False(picked.IsDeployable);
    }

    [Fact]
    public void Plan_RecordedMoveOntoAnIdTheModNowAddsThatCanMove_MovesThatIdOnward()
    {
        RemapMod first = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod updated = Installed(
            FileMod("b-oak", Map, MapAdding(NewEvent(3, "Merchant", Command(213, 4, 1, false)), NewEvent(4, "Smith", Command(213, 3, 1, false))), 1),
            MapBases);
        var recorded = new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([first, updated], [recorded], ["a-tin", "b-oak"], MapBases, null);
        MergePlan merged = ComposeRemapped(plan, MapBases, first, updated);

        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>4", "b-oak|MapEvent|data/map001.json|4>5"], plan.Remaps.Select(Entry));
        Assert.True(merged.IsDeployable);
        JToken merchant = merged.Ops.Single(item => item.Op.Key.Record == "event:4").Op.Value!;
        JToken smith = merged.Ops.Single(item => item.Op.Key.Record == "event:5").Op.Value!;
        Assert.Equal("Merchant", merchant["name"]!.Value<string>());
        Assert.Equal(5, merchant["pages"]![0]!["list"]![0]!["parameters"]![0]!.Value<int>());
        Assert.Equal("Smith", smith["name"]!.Value<string>());
        Assert.Equal(4, smith["pages"]![0]!["list"]![0]!["parameters"]![0]!.Value<int>());
    }

    [Fact]
    public void Plan_DuplicateRecordedEntries_OnlyTheStandingOneHoldsItsTarget()
    {
        RemapMod mod = Installed(FileMod("b-oak", Map, MapAdding(NewEvent(3, "Merchant"), null, null, NewEvent(6, "Smith"))), MapBases);
        IdRemap[] recorded =
        [
            new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 8),
            new IdRemap("b-oak", IdKind.MapEvent, MapKey, 6, 8),
            new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 7)
        ];

        RemapPlan plan = IdRemapPlanner.Plan([mod], recorded, ["b-oak"], MapBases, null);

        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>7", "b-oak|MapEvent|data/map001.json|6>8"], plan.Remaps.Select(Entry));
    }

    private static string Tree(params (int Id, string Name)[] added)
    {
        return EditArray(VanillaMapInfos, infos =>
        {
            foreach ((int id, string name) in added)
            {
                while (infos.Count < id)
                {
                    infos.Add(JValue.CreateNull());
                }

                infos.Add(new JObject { ["id"] = id, ["expanded"] = false, ["name"] = name, ["order"] = id, ["parentId"] = 0, ["scrollX"] = 0, ["scrollY"] = 0 });
            }
        });
    }

    [Fact]
    public void Plan_VanillaMapFileOutsideTheTree_IsNeverATarget()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos), Base("data/Map003.json", VanillaMap));
        RemapMod a = Installed(Mod("a-tin", 0, (NewMap, EditMap(map => map["displayName"] = "Cave")), (MapInfos, Tree((2, "Cave")))), bases);
        RemapMod b = Installed(Mod("b-oak", 1, (NewMap, EditMap(map => map["displayName"] = "Tower")), (MapInfos, Tree((2, "Tower")))), bases);

        RemapPlan plan = Plan(bases, a, b);

        Assert.Equal(["b-oak|Map||2>4"], plan.NewEntries.Select(Entry));
    }

    [Fact]
    public void Plan_TreeRecordForAVanillaMapFile_CannotMove()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos), Base(NewMap, VanillaMap));
        RemapMod a = Installed(Mod("a-tin", 0, (MapInfos, Tree((2, "Cave")))), bases);
        RemapMod b = Installed(Mod("b-oak", 1, (MapInfos, Tree((2, "Tower")))), bases);

        RemapPlan plan = Plan(bases, a, b);
        MergePlan merged = ComposeRemapped(plan, bases, a, b);

        Assert.Empty(plan.Remaps);
        Assert.Empty(plan.Offers);
        Collision clash = Assert.Single(merged.Collisions);
        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.False(merged.IsDeployable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RpgMakerEngine.MV)]
    public void Plan_MapPastTheEditorLimit_RefusesTheMove(RpgMakerEngine? engine)
    {
        (BaseFileSet bases, RemapMod a, RemapMod b) = MapClashAt(999);

        RemapPlan plan = Plan(engine, bases, a, b);

        Assert.Empty(plan.Remaps);
        Assert.False(ComposeRemapped(plan, bases, a, b).IsDeployable);
    }

    [Fact]
    public void Plan_MzMapPast999_MovesTo1000()
    {
        (BaseFileSet bases, RemapMod a, RemapMod b) = MapClashAt(999);

        RemapPlan plan = Plan(RpgMakerEngine.MZ, bases, a, b);
        MergePlan merged = ComposeRemapped(plan, bases, a, b);
        IReadOnlyList<MergedFile> files = MergedFileWriter.Write(merged, bases, null);

        Assert.Equal(["b-oak|Map||999>1000"], plan.NewEntries.Select(Entry));
        Assert.True(merged.IsDeployable);
        MergedFile moved = Assert.Single(files, file => file.File == "data/map1000.json");
        Assert.Equal("data/Map1000.json", moved.DiskPath);
        Assert.Equal("Tower", JObject.Parse(moved.Text!)["displayName"]!.Value<string>());
    }

    [Fact]
    public void Plan_MzMapPastTheEditorLimit_RefusesTheMove()
    {
        (BaseFileSet bases, RemapMod a, RemapMod b) = MapClashAt(2000);

        RemapPlan plan = Plan(RpgMakerEngine.MZ, bases, a, b);

        Assert.Empty(plan.Remaps);
        Assert.False(ComposeRemapped(plan, bases, a, b).IsDeployable);
    }

    /// <summary>Two mods add map <paramref name="id"/> to a tree whose highest id is the one below it.</summary>
    private static (BaseFileSet Bases, RemapMod A, RemapMod B) MapClashAt(int id)
    {
        string tree = EditArray(VanillaMapInfos, infos =>
        {
            while (infos.Count < id)
            {
                infos.Add(JValue.CreateNull());
            }
        });
        BaseFileSet bases = Bases(Base(MapInfos, tree));
        string file = "data/Map" + id.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        RemapMod a = Installed(Mod("a-tin", 0, (file, EditMap(map => map["displayName"] = "Cave"))), bases);
        RemapMod b = Installed(Mod("b-oak", 1, (file, EditMap(map => map["displayName"] = "Tower"))), bases);
        return (bases, a, b);
    }

    [Fact]
    public void Plan_MapOfferOnlyInAPluginFile_SettlesBothCollisionsWithOneWarning()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos));
        RemapMod a = Installed(
            Mod("a-tin", 0, (NewMap, EditMap(map => { map["displayName"] = "Cave"; map["note"] = "<link:2>"; })), (MapInfos, Tree((2, "Cave")))),
            bases);
        RemapMod b = Installed(
            Mod("b-oak", 1, (NewMap, EditMap(map => map["displayName"] = "Tower")), (MapInfos, Tree((2, "Tower")))),
            bases,
            scripts: "var floor = 2;");

        RemapPlan plan = Plan(bases, a, b);
        MergePlan merged = ComposeRemapped(plan, bases, a, b);

        Assert.Equal(["b-oak|Map||2>3"], plan.Offers.Select(Entry));
        Assert.Equal(2, merged.Collisions.Count);
        Assert.All(merged.Collisions, clash =>
        {
            Assert.Equal([ResolutionKind.PickMod, ResolutionKind.Remap, ResolutionKind.DisableMod], clash.Allowed);
            Assert.Equal(plan.Offers[0], plan.OfferFor(clash, "b-oak"));
        });
        Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapUnchecked);
    }

    [Fact]
    public void Apply_RewrittenNewDataFile_NamesItsModForTheCasing()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap), Base(MapInfos, VanillaMapInfos));
        const string Cutscenes = "data/Cutscenes.json";
        RemapMod a = Installed(Mod("a-tin", 0, (NewMap, EditMap(map => map["displayName"] = "Cave")), (MapInfos, Tree((2, "Cave")))), bases);
        RemapMod b = Installed(
            Mod(
                "b-oak",
                1,
                (NewMap, EditMap(map => map["displayName"] = "Tower")),
                (MapInfos, Tree((2, "Tower"))),
                (Cutscenes, new JArray(Command(201, 0, 2, 1, 1, 0, 0)).ToString())),
            bases);

        RemapPlan plan = Plan(bases, a, b);
        IReadOnlyList<MergedFile> files = MergedFileWriter.Write(ComposeRemapped(plan, bases, a, b), bases, null);

        Assert.Equal(["b-oak|Map||2>3"], plan.NewEntries.Select(Entry));
        MergedFile cutscenes = Assert.Single(files, file => file.File == "data/cutscenes.json");
        Assert.Equal(MergedFileSource.Text, cutscenes.Source);
        Assert.Equal("b-oak", cutscenes.ModId);
        Assert.Null(cutscenes.DiskPath);
        Assert.Equal("[0,3,1,1,0,0]", JArray.Parse(cutscenes.Text!)[0]!["parameters"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void Plan_DeployedModAgainstANewcomerHeldOnlyByAPluginFile_StaysAndOffersTheNewcomerAMove()
    {
        RemapMod deployed = Installed(FileMod("a-tin", Map, MapWith(NewEvent(3, "Guard")), 0), MapBases);
        RemapMod newcomer = Installed(FileMod("b-oak", Map, MapWith(NewEvent(3, "Merchant")), 1), MapBases, scripts: "var step = 3;");

        RemapPlan plan = IdRemapPlanner.Plan([deployed, newcomer], [], ["a-tin"], MapBases, null);
        MergePlan merged = ComposeRemapped(plan, MapBases, deployed, newcomer);

        Assert.Empty(plan.Remaps);
        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>4"], plan.Offers.Select(Entry));
        Collision clash = Assert.Single(merged.Collisions);
        Assert.True(clash.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.Remap, ResolutionKind.DisableMod], clash.Allowed);
        Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapUnchecked);
        Assert.False(merged.IsDeployable);
    }

    /// <summary>Vanilla's map after a game update: a new event 4 fills what was a hole.</summary>
    private static readonly string UpdatedMap = EditMapText(VanillaMap, map =>
    {
        var events = (JArray)map["events"]!;
        events.Add(JValue.CreateNull());
        events.Add(NewEvent(4, "Well"));
    });

    private static string UpdatedMapAdding(string name)
    {
        return EditMapText(UpdatedMap, map => map["events"]![3] = NewEvent(3, name));
    }

    [Fact]
    public void Plan_RecordedTargetVanillaNowUses_PlansTheIdAgainWithAWarningOnce()
    {
        BaseFileSet bases = Bases(Base(Map, UpdatedMap));
        RemapMod first = Installed(FileMod("a-tin", Map, UpdatedMapAdding("Guard"), 0), bases);
        RemapMod second = Installed(FileMod("b-oak", Map, UpdatedMapAdding("Merchant"), 1), bases);
        var recorded = new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 4);

        RemapPlan plan = IdRemapPlanner.Plan([first, second], [recorded], ["a-tin", "b-oak"], bases, null);
        MergePlan merged = ComposeRemapped(plan, bases, first, second);
        RemapPlan next = IdRemapPlanner.Plan([first, second], [recorded, .. plan.NewEntries], ["a-tin", "b-oak"], bases, null);

        Assert.Equal(["b-oak|MapEvent|data/map001.json|3>5"], plan.NewEntries.Select(Entry));
        Assert.Equal(plan.NewEntries, plan.Remaps);
        OperationDiagnostic warning = Assert.Single(merged.Diagnostics, item => item.Code == DiagnosticCodes.RulesRemapTargetTaken);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("b-oak", warning.Subject);
        Assert.True(merged.IsDeployable);
        Assert.DoesNotContain(merged.Ops, item => item.Op.Key.Record == "event:4");
        Assert.Empty(next.NewEntries);
        Assert.Empty(next.Diagnostics);
        Assert.Equal(plan.Remaps, next.Remaps);
    }

    [Fact]
    public void Plan_RecordedTargetVanillaNowUsesWithNoClash_LeavesTheIdWhereTheModPutIt()
    {
        BaseFileSet bases = Bases(Base(Map, UpdatedMap));
        RemapMod mod = Installed(FileMod("b-oak", Map, UpdatedMapAdding("Merchant"), 0), bases);

        RemapPlan plan = IdRemapPlanner.Plan([mod], [new IdRemap("b-oak", IdKind.MapEvent, MapKey, 3, 4)], ["b-oak"], bases, null);

        Assert.Empty(plan.Remaps);
        Assert.Equal(DiagnosticCodes.RulesRemapTargetTaken, Assert.Single(plan.Diagnostics).Code);
    }
}
