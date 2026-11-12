using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class MapMergeContractTests
{
    private const string VanillaMapJson =
        "{" +
        "\"autoplayBgm\":false,\"autoplayBgs\":false,\"battleback1Name\":\"\",\"battleback2Name\":\"\"," +
        "\"bgm\":{\"name\":\"\",\"pan\":0,\"pitch\":100,\"volume\":90}," +
        "\"bgs\":{\"name\":\"\",\"pan\":0,\"pitch\":100,\"volume\":90}," +
        "\"disableDashing\":false,\"displayName\":\"Meadow\",\"encounterList\":[],\"encounterStep\":30," +
        "\"height\":2,\"note\":\"\",\"parallaxLoopX\":false,\"parallaxLoopY\":false,\"parallaxName\":\"\"," +
        "\"parallaxShow\":true,\"parallaxSx\":0,\"parallaxSy\":0,\"scrollType\":0,\"specifyBattleback\":false," +
        "\"tilesetId\":1,\"width\":2,\"data\":[0,0,0,0]," +
        "\"events\":[" +
        "null," +
        "{\"id\":1,\"name\":\"Signpost\",\"note\":\"\",\"pages\":[" +
        "{\"conditions\":{\"actorId\":1,\"actorValid\":false,\"itemId\":1,\"itemValid\":false,\"selfSwitchCh\":\"A\",\"selfSwitchValid\":false,\"switch1Id\":1,\"switch1Valid\":false,\"switch2Id\":1,\"switch2Valid\":false,\"variableId\":1,\"variableValid\":false,\"variableValue\":0}," +
        "\"directionFix\":false,\"image\":{\"characterIndex\":0,\"characterName\":\"\",\"direction\":2,\"pattern\":0,\"tileId\":0}," +
        "\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}],\"moveFrequency\":3," +
        "\"moveRoute\":{\"list\":[{\"code\":0,\"parameters\":[]}],\"repeat\":true,\"skippable\":false,\"wait\":false}," +
        "\"moveSpeed\":3,\"moveType\":0,\"priorityType\":0,\"stepAnime\":false,\"through\":false,\"trigger\":0,\"walkAnime\":true}" +
        "],\"x\":0,\"y\":0}" +
        "]" +
        "}";

    [Fact]
    public void Build_MapEdit_GivesMapLevelOpsOnStarAndEventOpsOnEventId()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Map001.json", VanillaMapJson)
        });

        JObject modMap = JObject.Parse(VanillaMapJson);
        modMap["displayName"] = "Sunlit Meadow";
        modMap["bgm"]!["volume"] = 65;
        JObject event1 = (JObject)modMap["events"]![1]!;
        event1["x"] = 1;
        event1["pages"]![0]!["trigger"] = 2;
        event1["pages"]![0]!["conditions"]!["switch1Id"] = 5;

        ModSource mod = new(
            "a-map-edit",
            0,
            new[] { CreateModFile("data/Map001.json", "data/Map001.json", modMap.ToString()) },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Empty(changeSet.Diagnostics);

        RecordKey mapRootKey = new("data/map001.json", "*");
        RecordKey event1Key = new("data/map001.json", "event:1");

        ChangeOp displayNameOp = Assert.Single(changeSet.Ops, op => op.Key == mapRootKey && op.Path == "displayName");
        Assert.Equal(ChangeKind.SetLeaf, displayNameOp.Kind);
        Assert.Equal("Sunlit Meadow", displayNameOp.Value!.Value<string>());

        ChangeOp bgmVolumeOp = Assert.Single(changeSet.Ops, op => op.Key == mapRootKey && op.Path == "bgm.volume");
        Assert.Equal(ChangeKind.SetLeaf, bgmVolumeOp.Kind);
        Assert.Equal(65, bgmVolumeOp.Value!.Value<int>());

        ChangeOp xOp = Assert.Single(changeSet.Ops, op => op.Key == event1Key && op.Path == "x");
        Assert.Equal(ChangeKind.SetLeaf, xOp.Kind);
        Assert.Equal(1, xOp.Value!.Value<int>());

        ChangeOp triggerOp = Assert.Single(changeSet.Ops, op => op.Key == event1Key && op.Path == "pages[0].trigger");
        Assert.Equal(ChangeKind.SetLeaf, triggerOp.Kind);
        Assert.Equal(2, triggerOp.Value!.Value<int>());

        ChangeOp switchOp = Assert.Single(changeSet.Ops, op => op.Key == event1Key && op.Path == "pages[0].conditions.switch1Id");
        Assert.Equal(ChangeKind.SetLeaf, switchOp.Kind);
        Assert.Equal(5, switchOp.Value!.Value<int>());
    }

    [Fact]
    public void Compose_PageCountChangeByTwoMods_IsAPageClash()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Map001.json", VanillaMapJson)
        });

        JObject modMapA = JObject.Parse(VanillaMapJson);
        JArray pagesA = (JArray)modMapA["events"]![1]!["pages"]!;
        JObject extraPageA = (JObject)pagesA[0].DeepClone();
        extraPageA["trigger"] = 1;
        pagesA.Add(extraPageA);

        JObject modMapB = JObject.Parse(VanillaMapJson);
        JArray pagesB = (JArray)modMapB["events"]![1]!["pages"]!;
        JObject extraPageB = (JObject)pagesB[0].DeepClone();
        extraPageB["trigger"] = 3;
        pagesB.Add(extraPageB);

        ModChangeSet csA = ChangeSetBuilder.Build(
            new ModSource(
                "a-ash",
                0,
                new[] { CreateModFile("data/Map001.json", "data/Map001.json", modMapA.ToString()) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ModChangeSet csB = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { CreateModFile("data/Map001.json", "data/Map001.json", modMapB.ToString()) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Collision clash = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.PageClash, clash.Kind);
        Assert.Equal(new RecordKey("data/map001.json", "event:1"), clash.Key);
        Assert.Equal("pages", clash.Path);
        Assert.True(clash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.PickVanilla }, clash.Allowed);
    }

    [Fact]
    public void Compose_MapEventPageSettingVersusListClash_DistinguishesPageClashFromLogicClash()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Map001.json", VanillaMapJson)
        });

        RecordKey event1Key = new("data/map001.json", "event:1");

        ModChangeSet csA = new(
            "a-ash",
            0,
            new[]
            {
                new ChangeOp("a-ash", event1Key, "pages[0].trigger", ChangeKind.SetLeaf, new JValue(1), "data/Map001.json"),
                new ChangeOp("a-ash", event1Key, "pages[0].list", ChangeKind.EditCommands, JArray.Parse("[{\"code\":101,\"indent\":0,\"parameters\":[\"\",0,0,2]},{\"code\":0,\"indent\":0,\"parameters\":[]}]"), "data/Map001.json")
            },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csB = new(
            "b-birch",
            1,
            new[]
            {
                new ChangeOp("b-birch", event1Key, "pages[0].trigger", ChangeKind.SetLeaf, new JValue(2), "data/Map001.json"),
                new ChangeOp("b-birch", event1Key, "pages[0].list", ChangeKind.EditCommands, JArray.Parse("[{\"code\":201,\"indent\":0,\"parameters\":[0,1,0,0,0,0]},{\"code\":0,\"indent\":0,\"parameters\":[]}]"), "data/Map001.json")
            },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Assert.Equal(2, plan.Collisions.Count);

        Collision pageClash = Assert.Single(plan.Collisions, c => c.Path == "pages[0].trigger");
        Assert.Equal(CollisionKind.PageClash, pageClash.Kind);
        Assert.True(pageClash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.PickVanilla }, pageClash.Allowed);

        Collision logicClash = Assert.Single(plan.Collisions, c => c.Path == "pages[0].list");
        Assert.Equal(CollisionKind.LogicClash, logicClash.Kind);
        Assert.True(logicClash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.PickVanilla }, logicClash.Allowed);
    }

    [Fact]
    public void Compose_NewEventAtSameIdWithDifferentContent_IsAnIdClash()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Map001.json", VanillaMapJson)
        });

        JObject modMapA = JObject.Parse(VanillaMapJson);
        JObject newEventA = (JObject)modMapA["events"]![1]!.DeepClone();
        newEventA["id"] = 2;
        newEventA["name"] = "ChestA";
        newEventA["x"] = 1;
        ((JArray)modMapA["events"]!).Add(newEventA);

        JObject modMapB = JObject.Parse(VanillaMapJson);
        JObject newEventB = (JObject)modMapB["events"]![1]!.DeepClone();
        newEventB["id"] = 2;
        newEventB["name"] = "ChestB";
        newEventB["y"] = 1;
        ((JArray)modMapB["events"]!).Add(newEventB);

        ModChangeSet csA = ChangeSetBuilder.Build(
            new ModSource(
                "a-ash",
                0,
                new[] { CreateModFile("data/Map001.json", "data/Map001.json", modMapA.ToString()) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ModChangeSet csB = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { CreateModFile("data/Map001.json", "data/Map001.json", modMapB.ToString()) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ChangeOp addOpA = Assert.Single(csA.Ops);
        Assert.Equal(new RecordKey("data/map001.json", "event:2"), addOpA.Key);
        Assert.Equal("", addOpA.Path);
        Assert.Equal(ChangeKind.AddRecord, addOpA.Kind);

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Collision clash = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.Equal(new RecordKey("data/map001.json", "event:2"), clash.Key);
        Assert.Equal("", clash.Path);
        Assert.True(clash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.DisableMod }, clash.Allowed);
    }

    private static BaseFile CreateBaseFile(string path, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new BaseFile(path, text, sha);
    }

    private static ModFile CreateModFile(string target, string source, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ModFile(target, source, text, sha);
    }
}
