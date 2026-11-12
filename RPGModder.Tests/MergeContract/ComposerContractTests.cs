using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class ComposerContractTests
{
    [Fact]
    public void Compose_EqualAddRecords_AreOneOpWithoutCollision()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]")
        });

        JObject newRecordA = JObject.Parse("{\"id\":2,\"name\":\"Elixir\",\"price\":500}");
        JObject newRecordB = JObject.Parse("{\"id\":2,\"name\":\"Elixir\",\"price\":500}");
        RecordKey key = new("data/items.json", "id:2");

        ModChangeSet csA = new(
            "a-mod",
            0,
            new[] { new ChangeOp("a-mod", key, "", ChangeKind.AddRecord, newRecordA, "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csB = new(
            "b-mod",
            1,
            new[] { new ChangeOp("b-mod", key, "", ChangeKind.AddRecord, newRecordB, "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.True(plan.IsDeployable);
        Assert.Empty(plan.Collisions);
        MergedOp mergedOp = Assert.Single(plan.Ops);
        Assert.Equal(ChangeKind.AddRecord, mergedOp.Op.Kind);
        Assert.Equal(new[] { "a-mod", "b-mod" }, mergedOp.ModIds);
    }

    [Fact]
    public void Compose_DifferentSequenceAndCommandEdits_AreSequenceAndLogicClashes()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"effects\":[]}]"),
            CreateBaseFile("data/CommonEvents.json", "[null,{\"id\":1,\"name\":\"Evt\",\"list\":[]}]")
        });

        RecordKey itemKey = new("data/items.json", "id:1");
        RecordKey eventKey = new("data/commonevents.json", "id:1");

        ModChangeSet csA = new(
            "a-mod",
            0,
            new[]
            {
                new ChangeOp("a-mod", itemKey, "effects", ChangeKind.EditSequence, JArray.Parse("[{\"code\":11}]"), "data/Items.json"),
                new ChangeOp("a-mod", eventKey, "list", ChangeKind.EditCommands, JArray.Parse("[{\"code\":101},{\"code\":0}]"), "data/CommonEvents.json")
            },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csB = new(
            "b-mod",
            1,
            new[]
            {
                new ChangeOp("b-mod", itemKey, "effects", ChangeKind.EditSequence, JArray.Parse("[{\"code\":12}]"), "data/Items.json"),
                new ChangeOp("b-mod", eventKey, "list", ChangeKind.EditCommands, JArray.Parse("[{\"code\":201},{\"code\":0}]"), "data/CommonEvents.json")
            },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Assert.Equal(2, plan.Collisions.Count);

        Collision seqClash = Assert.Single(plan.Collisions, c => c.Kind == CollisionKind.SequenceClash);
        Assert.True(seqClash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.PickVanilla }, seqClash.Allowed);

        Collision logicClash = Assert.Single(plan.Collisions, c => c.Kind == CollisionKind.LogicClash);
        Assert.True(logicClash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.PickVanilla }, logicClash.Allowed);
    }

    [Fact]
    public void Compose_PrefixPathsAcrossMods_AreAValueClashOnTheShorterPath()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", "{\"gameTitle\":\"Sample\",\"advanced\":{\"gameId\":1,\"screenWidth\":816}}")
        });

        RecordKey sysKey = new("data/system.json", "*");

        ModChangeSet csA = new(
            "a-mod",
            0,
            new[] { new ChangeOp("a-mod", sysKey, "advanced", ChangeKind.SetLeaf, JObject.Parse("{\"gameId\":2}"), "data/System.json") },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csB = new(
            "b-mod",
            1,
            new[] { new ChangeOp("b-mod", sysKey, "advanced.gameId", ChangeKind.SetLeaf, new JValue(3), "data/System.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.ValueClash, collision.Kind);
        Assert.Equal("advanced", collision.Path);
        Assert.False(plan.IsDeployable);
    }

    [Fact]
    public void Compose_ErrorDiagnostic_MakesThePlanNotDeployable()
    {
        BaseFileSet baseFiles = BaseFileSet.From(Array.Empty<BaseFile>());

        ModChangeSet cs = new(
            "a-broken",
            0,
            Array.Empty<ChangeOp>(),
            new[]
            {
                new OperationDiagnostic(
                    DiagnosticCodes.MergeParseFailed,
                    "Broken JSON.",
                    DiagnosticSeverity.Error,
                    "a-broken")
            });

        MergePlan plan = Composer.Compose(new[] { cs }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Assert.Contains(plan.Diagnostics, d => d.Code == DiagnosticCodes.MergeParseFailed);
    }

    [Fact]
    public void Compose_ShuffledInputs_GiveTheSamePlan()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile(
                "data/Items.json",
                "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50,\"note\":\"\"},{\"id\":2,\"name\":\"Tonic\",\"price\":120,\"note\":\"\"}]")
        });

        RecordKey item1 = new("data/items.json", "id:1");
        RecordKey item2 = new("data/items.json", "id:2");

        ModChangeSet csA = new(
            "a-alpha",
            0,
            new[]
            {
                new ChangeOp("a-alpha", item1, "price", ChangeKind.SetLeaf, new JValue(70), "data/Items.json"),
                new ChangeOp("a-alpha", item2, "price", ChangeKind.SetLeaf, new JValue(140), "data/Items.json")
            },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csB = new(
            "b-beta",
            1,
            new[]
            {
                new ChangeOp("b-beta", item1, "note", ChangeKind.SetLeaf, new JValue("fresh"), "data/Items.json"),
                new ChangeOp("b-beta", item2, "price", ChangeKind.SetLeaf, new JValue(180), "data/Items.json")
            },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csC = new(
            "c-gamma",
            2,
            new[]
            {
                new ChangeOp("c-gamma", item1, "price", ChangeKind.SetLeaf, new JValue(90), "data/Items.json"),
                new ChangeOp("c-gamma", item1, "note", ChangeKind.SetLeaf, new JValue("fresh"), "data/Items.json")
            },
            Array.Empty<OperationDiagnostic>());

        MergePlan unshuffledUnresolved = Composer.Compose(new[] { csA, csB, csC }, Array.Empty<Resolution>(), baseFiles);
        Assert.Equal(2, unshuffledUnresolved.Collisions.Count);

        Resolution res1 = new(unshuffledUnresolved.Collisions[0].Id, ResolutionKind.PickMod, unshuffledUnresolved.Collisions[0].ModIds[0]);
        Resolution res2 = new(unshuffledUnresolved.Collisions[1].Id, ResolutionKind.PickVanilla, null);

        MergePlan baseline = Composer.Compose(new[] { csA, csB, csC }, new[] { res1, res2 }, baseFiles);
        MergePlan shuffled = Composer.Compose(new[] { csC, csA, csB }, new[] { res2, res1 }, baseFiles);

        Assert.Equal(baseline.MergeHash, shuffled.MergeHash);
        Assert.Equal(baseline.IsDeployable, shuffled.IsDeployable);
        Assert.Equal(baseline.Ops.Count, shuffled.Ops.Count);

        for (int i = 0; i < baseline.Ops.Count; i++)
        {
            Assert.Equal(baseline.Ops[i].Op.Key, shuffled.Ops[i].Op.Key);
            Assert.Equal(baseline.Ops[i].Op.Path, shuffled.Ops[i].Op.Path);
            Assert.Equal(baseline.Ops[i].ModIds, shuffled.Ops[i].ModIds);
            Assert.True(JToken.DeepEquals(baseline.Ops[i].Op.Value, shuffled.Ops[i].Op.Value));
        }
    }

    private static BaseFile CreateBaseFile(string path, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new BaseFile(path, text, sha);
    }
}
