using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class ChangeSetBuilderContractTests
{
    [Fact]
    public void Build_VolatileFieldsViaFileOps_GiveNoOps()
    {
        const string vanillaSystem =
            "{\"gameTitle\":\"Sample\",\"versionId\":1,\"editMapId\":1,\"screenWidth\":816,\"screenHeight\":624," +
            "\"uiAreaWidth\":816,\"uiAreaHeight\":624,\"windowLineWidth\":12,\"locale\":\"en_US\"," +
            "\"title1Name\":\"Castle\",\"title2Name\":\"Gate\",\"currencyUnit\":\"G\"}";
        const string modSystem =
            "{\"gameTitle\":\"Sample\",\"versionId\":99,\"editMapId\":5,\"screenWidth\":1280,\"screenHeight\":720," +
            "\"uiAreaWidth\":1280,\"uiAreaHeight\":720,\"windowLineWidth\":16,\"locale\":\"ja_JP\"," +
            "\"title1Name\":\"Tower\",\"title2Name\":\"Dawn\",\"currencyUnit\":\"G\"}";

        const string vanillaMapInfos =
            "[null,{\"id\":1,\"name\":\"Town\",\"parentId\":0,\"order\":1,\"expanded\":false,\"scrollX\":400,\"scrollY\":300}]";
        const string modMapInfos =
            "[null,{\"id\":1,\"name\":\"Town\",\"parentId\":0,\"order\":1,\"expanded\":true,\"scrollX\":850,\"scrollY\":610}]";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", vanillaSystem),
            CreateBaseFile("data/MapInfos.json", vanillaMapInfos)
        });

        ModSource mod = new(
            "a-volatile",
            0,
            new[]
            {
                CreateModFile("data/System.json", "data/System.json", modSystem),
                CreateModFile("data/MapInfos.json", "data/MapInfos.json", modMapInfos)
            },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Empty(changeSet.Diagnostics);
        Assert.Empty(changeSet.Ops);
    }

    [Fact]
    public void Build_VolatileFieldsViaJsonPatches_GiveNoOps()
    {
        const string vanillaSystem =
            "{\"gameTitle\":\"Sample\",\"versionId\":1,\"editMapId\":1,\"screenWidth\":816,\"screenHeight\":624," +
            "\"uiAreaWidth\":816,\"uiAreaHeight\":624,\"windowLineWidth\":12,\"locale\":\"en_US\"," +
            "\"title1Name\":\"Castle\",\"title2Name\":\"Gate\",\"currencyUnit\":\"G\"}";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", vanillaSystem)
        });

        JObject patchData = JObject.Parse(
            "{\"versionId\":42,\"editMapId\":9,\"screenWidth\":1920,\"screenHeight\":1080," +
            "\"uiAreaWidth\":1920,\"uiAreaHeight\":1080,\"windowLineWidth\":20,\"locale\":\"es_CL\"," +
            "\"title1Name\":\"Alt1\",\"title2Name\":\"Alt2\"}");

        ModSource mod = new(
            "a-patch-volatile",
            0,
            Array.Empty<ModFile>(),
            new[] { new ModPatch("data/System.json", patchData) },
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Empty(changeSet.Diagnostics);
        Assert.Empty(changeSet.Ops);
    }

    [Fact]
    public void Build_BrokenModJson_IsParseFailedOnTheModWithNoOp()
    {
        const string vanillaItems = "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]";
        const string vanillaWeapons = "[null,{\"id\":1,\"name\":\"Short Sword\",\"price\":100}]";
        const string modWeapons = "[null,{\"id\":1,\"name\":\"Short Sword\",\"price\":140}]";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", vanillaItems),
            CreateBaseFile("data/Weapons.json", vanillaWeapons)
        });

        ModSource mod = new(
            "a-broken",
            0,
            new[]
            {
                CreateModFile("data/Items.json", "data/Items.json", "[null, {\"id\": 1, broken json"),
                CreateModFile("data/Weapons.json", "data/Weapons.json", modWeapons)
            },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        OperationDiagnostic diag = Assert.Single(changeSet.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Equal("a-broken", diag.Subject);

        Assert.DoesNotContain(changeSet.Ops, op => op.Key.File == "data/items.json");
        ChangeOp weaponOp = Assert.Single(changeSet.Ops);
        Assert.Equal(new RecordKey("data/weapons.json", "id:1"), weaponOp.Key);
        Assert.Equal("price", weaponOp.Path);
    }

    [Fact]
    public void Build_PatchOnAMissingFile_IsPatchTargetMissing()
    {
        BaseFileSet baseFiles = BaseFileSet.From(Array.Empty<BaseFile>());

        ModSource mod = new(
            "a-orphan-patch",
            0,
            Array.Empty<ModFile>(),
            new[] { new ModPatch("data/System.json", JObject.Parse("{\"currencyUnit\":\"Silver\"}")) },
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        OperationDiagnostic diag = Assert.Single(changeSet.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergePatchTargetMissing, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Empty(changeSet.Ops);
    }

    [Fact]
    public void Build_ModFileRootShapeDiffersFromBase_IsRootShapeMismatch()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]")
        });

        ModSource mod = new(
            "a-wrong-root",
            0,
            new[] { CreateModFile("data/Items.json", "data/Items.json", "{\"id\":1,\"name\":\"Herb\"}") },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        OperationDiagnostic diag = Assert.Single(changeSet.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Empty(changeSet.Ops);
    }

    [Fact]
    public void Build_PatchOnArrayRootFile_IsPatchShapeMismatch()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]")
        });

        ModSource mod = new(
            "a-wrong-patch",
            0,
            Array.Empty<ModFile>(),
            new[] { new ModPatch("data/Items.json", JObject.Parse("{\"price\":99}")) },
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        OperationDiagnostic diag = Assert.Single(changeSet.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergePatchShapeMismatch, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Empty(changeSet.Ops);
    }

    [Fact]
    public void Build_WholeFileAndEqualPatch_GiveTheSameOps()
    {
        const string vanillaSystem =
            "{\"gameTitle\":\"Sample\",\"currencyUnit\":\"G\",\"testBattlers\":[],\"advanced\":{\"gameId\":10,\"Categ\":1}}";
        const string modSystem =
            "{\"gameTitle\":\"SampleUpdated\",\"currencyUnit\":\"Coins\",\"testBattlers\":[],\"advanced\":{\"gameId\":25,\"Categ\":1}}";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", vanillaSystem)
        });

        ModSource fileMod = new(
            "a-mod",
            0,
            new[] { CreateModFile("data/System.json", "data/System.json", modSystem) },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModSource patchMod = new(
            "a-mod",
            0,
            Array.Empty<ModFile>(),
            new[]
            {
                new ModPatch(
                    "data/System.json",
                    JObject.Parse("{\"gameTitle\":\"SampleUpdated\",\"currencyUnit\":\"Coins\",\"advanced\":{\"gameId\":25}}"))
            },
            Array.Empty<PluginEntry>());

        ModChangeSet fromFile = ChangeSetBuilder.Build(fileMod, baseFiles);
        ModChangeSet fromPatch = ChangeSetBuilder.Build(patchMod, baseFiles);

        Assert.Empty(fromFile.Diagnostics);
        Assert.Empty(fromPatch.Diagnostics);
        Assert.Equal(fromFile.Ops.Count, fromPatch.Ops.Count);

        for (int i = 0; i < fromFile.Ops.Count; i++)
        {
            ChangeOp a = fromFile.Ops[i];
            ChangeOp b = fromPatch.Ops[i];
            Assert.Equal(a.ModId, b.ModId);
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.Path, b.Path);
            Assert.Equal(a.Kind, b.Kind);
            Assert.True(JToken.DeepEquals(a.Value, b.Value));
        }
    }

    [Fact]
    public void Build_SequenceAndCommandArrays_GiveEditSequenceAndEditCommands()
    {
        const string vanillaItems = "[null,{\"id\":1,\"name\":\"Herb\",\"effects\":[{\"code\":11,\"dataId\":0,\"value1\":10,\"value2\":0}]}]";
        const string modItems = "[null,{\"id\":1,\"name\":\"Herb\",\"effects\":[{\"code\":11,\"dataId\":0,\"value1\":25,\"value2\":0}]}]";

        const string vanillaCommonEvents = "[null,{\"id\":1,\"name\":\"Intro\",\"trigger\":0,\"switchId\":1,\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}]}]";
        const string modCommonEvents = "[null,{\"id\":1,\"name\":\"Intro\",\"trigger\":0,\"switchId\":1,\"list\":[{\"code\":101,\"indent\":0,\"parameters\":[\"\",0,0,2]},{\"code\":0,\"indent\":0,\"parameters\":[]}]}]";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", vanillaItems),
            CreateBaseFile("data/CommonEvents.json", vanillaCommonEvents)
        });

        ModSource mod = new(
            "a-seq",
            0,
            new[]
            {
                CreateModFile("data/Items.json", "data/Items.json", modItems),
                CreateModFile("data/CommonEvents.json", "data/CommonEvents.json", modCommonEvents)
            },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Equal(2, changeSet.Ops.Count);
        ChangeOp effectsOp = Assert.Single(changeSet.Ops, op => op.Key.File == "data/items.json");
        Assert.Equal(new RecordKey("data/items.json", "id:1"), effectsOp.Key);
        Assert.Equal("effects", effectsOp.Path);
        Assert.Equal(ChangeKind.EditSequence, effectsOp.Kind);

        ChangeOp listOp = Assert.Single(changeSet.Ops, op => op.Key.File == "data/commonevents.json");
        Assert.Equal(new RecordKey("data/commonevents.json", "id:1"), listOp.Key);
        Assert.Equal("list", listOp.Path);
        Assert.Equal(ChangeKind.EditCommands, listOp.Kind);
    }

    [Fact]
    public void Build_Twice_GivesEqualSerializedText()
    {
        const string vanillaItems = "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50,\"note\":\"\"}]";
        const string modItems = "[null,{\"id\":1,\"name\":\"Herb\",\"price\":85,\"note\":\"mint\"},{\"id\":2,\"name\":\"Sap\",\"price\":120,\"note\":\"\"}]";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", vanillaItems)
        });

        ModSource mod = new(
            "a-repeat",
            2,
            new[] { CreateModFile("data/Items.json", "data/Items.json", modItems) },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet first = ChangeSetBuilder.Build(mod, baseFiles);
        ModChangeSet second = ChangeSetBuilder.Build(mod, baseFiles);

        string firstSerialized = ModChangeSetSerializer.Serialize(first);
        string secondSerialized = ModChangeSetSerializer.Serialize(second);

        Assert.Equal(firstSerialized, secondSerialized);
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
