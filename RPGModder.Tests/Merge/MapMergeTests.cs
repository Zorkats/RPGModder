using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class MapMergeTests
{
    private static JObject Event(JObject map, int id)
    {
        return (JObject)map["events"]![id]!;
    }

    private static JObject Page(JObject map, int id)
    {
        return (JObject)Event(map, id)["pages"]![0]!;
    }

    [Fact]
    public void Build_MapEdit_GivesMapLevelOpsOnStarAndEventOpsOnTheEvent()
    {
        ModSource mod = FileMod("a-tin", Map, EditMap(map =>
        {
            map["displayName"] = "Meadow";
            map["data"]![2] = 9;
            Event(map, 1)["x"] = 1;
            Page(map, 1)["trigger"] = 1;
        }));

        ModChangeSet set = Build(mod, Bases(Base(Map, VanillaMap)));

        Assert.Empty(set.Diagnostics);
        Assert.Equal(
            [
                "data/map001.json|*|data[2]|SetLeaf|9",
                "data/map001.json|*|displayName|SetLeaf|\"Meadow\"",
                "data/map001.json|event:1|pages[0].trigger|SetLeaf|1",
                "data/map001.json|event:1|x|SetLeaf|1"
            ],
            set.Ops.Select(Describe));
    }

    [Fact]
    public void Build_MoveRouteChange_IsOneValue()
    {
        ModSource mod = FileMod("a-tin", Map, EditMap(map => Page(map, 1)["moveRoute"]!["repeat"] = false));

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Map, VanillaMap))).Ops);

        Assert.Equal("pages[0].moveRoute", op.Path);
        Assert.Equal(ChangeKind.SetLeaf, op.Kind);
        Assert.False(op.Value!["repeat"]!.Value<bool>());
    }

    [Fact]
    public void Build_CommandListChange_IsOneEditCommands()
    {
        ModSource mod = FileMod("a-tin", Map, EditMap(map => Page(map, 2)["list"]![0]!["indent"] = 1));

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Map, VanillaMap))).Ops);

        Assert.Equal("event:2", op.Key.Record);
        Assert.Equal("pages[0].list", op.Path);
        Assert.Equal(ChangeKind.EditCommands, op.Kind);
    }

    [Fact]
    public void Build_PageCountChange_IsOneValueAtPages()
    {
        ModSource mod = FileMod("a-tin", Map, EditMap(map => ((JArray)Event(map, 1)["pages"]!).Add(Page(map, 1).DeepClone())));

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Map, VanillaMap))).Ops);

        Assert.Equal("event:1|pages", op.Key.Record + "|" + op.Path);
        Assert.Equal(2, ((JArray)op.Value!).Count);
    }

    [Fact]
    public void Build_NewAndRemovedEvents_AreRecordOps()
    {
        ModSource mod = FileMod("a-tin", Map, EditMap(map =>
        {
            map["events"]![1] = JValue.CreateNull();
            ((JArray)map["events"]!).Add(new JObject { ["id"] = 3, ["name"] = "Well" });
        }));

        Assert.Equal(
            ["data/map001.json|event:1||RemoveRecord|-", "data/map001.json|event:3||AddRecord|{\"id\":3,\"name\":\"Well\"}"],
            Build(mod, Bases(Base(Map, VanillaMap))).Ops.Select(Describe));
    }

    [Fact]
    public void Build_EventsThatAreNotRecords_AreOneMapLevelValue()
    {
        string vanilla = EditMap(map => map["events"] = new JArray(JValue.CreateNull(), new JObject { ["id"] = 5 }));
        ModSource mod = FileMod("a-tin", Map, EditMap(map => map["events"] = new JArray(JValue.CreateNull(), new JObject { ["id"] = 6 })));

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Map, vanilla))).Ops);

        Assert.Equal("*|events", op.Key.Record + "|" + op.Path);
    }

    [Fact]
    public void Build_MapWithOnlyNullEvents_StillTakesEventRecords()
    {
        string vanilla = EditMap(map => map["events"] = new JArray(JValue.CreateNull()));
        ModSource mod = FileMod("a-tin", Map, EditMap(map => map["events"] = new JArray(JValue.CreateNull(), new JObject { ["id"] = 1 })));

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Map, vanilla))).Ops);

        Assert.Equal("event:1", op.Key.Record);
        Assert.Equal(ChangeKind.AddRecord, op.Kind);
    }

    [Fact]
    public void Build_MapWithAnArrayRoot_IsRootShapeMismatch()
    {
        ModChangeSet set = Build(FileMod("a-tin", Map, "[]"), Bases(Base(Map, VanillaMap)));

        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, Assert.Single(set.Diagnostics).Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"x\"")]
    [InlineData("missing")]
    public void Compose_ModEventsThatAreNotAList_AreRefusedNotMergedOverAnEventEdit(string events)
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource broken = FileMod("a-tin", Map, EditMap(map =>
        {
            if (events == "missing")
            {
                map.Remove("events");
            }
            else
            {
                map["events"] = JToken.Parse(events);
            }
        }));
        ModSource editor = FileMod("b-oak", Map, EditMap(map => Event(map, 1)["x"] = 9));

        MergePlan plan = Compose(bases, broken, editor);

        OperationDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, diagnostic.Code);
        Assert.Equal("a-tin", diagnostic.Subject);
        Assert.False(plan.IsDeployable);
        Assert.All(plan.Ops, op => Assert.Equal(["b-oak"], op.ModIds));
    }

    [Fact]
    public void Build_PatchThatTurnsEventsIntoAnObject_IsRefused()
    {
        ModChangeSet set = Build(PatchMod("a-tin", Map, "{\"events\":{\"1\":{}}}"), Bases(Base(Map, VanillaMap)));

        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, Assert.Single(set.Diagnostics).Code);
    }

    [Theory]
    [InlineData("data/map001.json", true)]
    [InlineData("data/map1.json", true)]
    [InlineData("data/mapinfos.json", false)]
    [InlineData("data/map.json", false)]
    [InlineData("data/map1a.json", false)]
    [InlineData("data/sub/map001.json", false)]
    public void IsMapFile_TakesOnlyMapAndDigits(string key, bool expected)
    {
        Assert.Equal(expected, RpgDataSchema.IsMapFile(key));
    }

    [Fact]
    public void Compose_PageCountChangedByTwoMods_IsAPageClash()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => ((JArray)Event(map, 1)["pages"]!).Add(Page(map, 1).DeepClone())));
        ModSource second = FileMod("b-oak", Map, EditMap(map => Event(map, 1)["pages"] = new JArray()));

        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        Assert.Equal(CollisionKind.PageClash, collision.Kind);
        Assert.Equal("pages", collision.Path);
        Assert.True(collision.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.PickVanilla], collision.Allowed);
    }

    [Fact]
    public void Compose_PageSettingChangedByTwoMods_IsAPageClash()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => Page(map, 1)["trigger"] = 3));
        ModSource second = FileMod("b-oak", Map, EditMap(map => Page(map, 1)["trigger"] = 4));

        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        Assert.Equal(CollisionKind.PageClash, collision.Kind);
        Assert.Equal("pages[0].trigger", collision.Path);
    }

    [Fact]
    public void Compose_PageSettingAndCommandEditOnOnePage_MergeWithoutACollision()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => Page(map, 1)["trigger"] = 3));
        ModSource second = FileMod("b-oak", Map, EditMap(map => Page(map, 1)["list"]![0]!["indent"] = 1));

        MergePlan plan = Compose(bases, first, second);

        Assert.Empty(plan.Collisions);
        Assert.Equal(2, plan.Ops.Count);
    }

    [Fact]
    public void Compose_TwoCommandEditsOnOnePage_AreALogicClash()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => Page(map, 1)["list"]![0]!["indent"] = 1));
        ModSource second = FileMod("b-oak", Map, EditMap(map => Page(map, 1)["list"]![0]!["indent"] = 2));

        Assert.Equal(CollisionKind.LogicClash, Assert.Single(Compose(bases, first, second).Collisions).Kind);
    }

    [Fact]
    public void Compose_NewEventAtOneIdFromTwoMods_IsAnIdClash()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => ((JArray)map["events"]!).Add(new JObject { ["id"] = 3, ["name"] = "Well" })));
        ModSource second = FileMod("b-oak", Map, EditMap(map => ((JArray)map["events"]!).Add(new JObject { ["id"] = 3, ["name"] = "Tree" })));

        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        Assert.Equal(CollisionKind.IdClash, collision.Kind);
        Assert.Equal("event:3", collision.Key.Record);
    }

    [Fact]
    public void Write_EventEditsFromTwoMods_LandInTheirEvents()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource first = FileMod("a-tin", Map, EditMap(map => Event(map, 1)["x"] = 1));
        ModSource second = FileMod("b-oak", Map, EditMap(map => Event(map, 2)["name"] = "Crate"));
        MergePlan plan = Compose(bases, first, second);

        MergedFile file = Assert.Single(MergedFileWriter.Write(plan, bases, null));

        JObject expected = JObject.Parse(EditMap(map =>
        {
            Event(map, 1)["x"] = 1;
            Event(map, 2)["name"] = "Crate";
        }));
        Assert.Equal(MergeJson.Write(expected), file.Text);
        Assert.Equal(["a-tin", "b-oak"], file.ModIds);
    }

    [Fact]
    public void Write_RemovedLastEvent_IsCutAndAnEarlierOneBecomesNull()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource last = FileMod("a-tin", Map, EditMap(map => ((JArray)map["events"]!).RemoveAt(2)));
        ModSource middle = FileMod("a-tin", Map, EditMap(map => map["events"]![1] = JValue.CreateNull()));

        JArray cut = (JArray)JObject.Parse(Assert.Single(MergedFileWriter.Write(Compose(bases, last), bases, null)).Text!)["events"]!;
        JArray hole = (JArray)JObject.Parse(Assert.Single(MergedFileWriter.Write(Compose(bases, middle), bases, null)).Text!)["events"]!;

        Assert.Equal(2, cut.Count);
        Assert.Equal(3, hole.Count);
        Assert.Equal(JTokenType.Null, hole[1].Type);
    }

    [Fact]
    public void Write_NewEventPastTheEnd_PadsWithNull()
    {
        BaseFileSet bases = Bases(Base(Map, VanillaMap));
        ModSource mod = FileMod("a-tin", Map, EditMap(map =>
        {
            ((JArray)map["events"]!).Add(JValue.CreateNull());
            ((JArray)map["events"]!).Add(new JObject { ["id"] = 4 });
        }));

        JArray events = (JArray)JObject.Parse(Assert.Single(MergedFileWriter.Write(Compose(bases, mod), bases, null)).Text!)["events"]!;

        Assert.Equal(5, events.Count);
        Assert.Equal(JTokenType.Null, events[3].Type);
    }
}
