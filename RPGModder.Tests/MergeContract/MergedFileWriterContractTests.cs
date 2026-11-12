using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class MergedFileWriterContractTests
{
    [Fact]
    public void Write_PlanNotDeployable_Throws()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]")
        });

        RecordKey key = new("data/items.json", "id:1");

        ModChangeSet csA = new(
            "a-mod",
            0,
            new[] { new ChangeOp("a-mod", key, "price", ChangeKind.SetLeaf, new JValue(60), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csB = new(
            "b-mod",
            1,
            new[] { new ChangeOp("b-mod", key, "price", ChangeKind.SetLeaf, new JValue(70), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(plan.IsDeployable);
        Assert.Throws<InvalidOperationException>(() => MergedFileWriter.Write(plan, baseFiles, null));
    }

    [Fact]
    public void Write_VersionId_ListsSystemOnlyWhenGiven()
    {
        const string vanillaSystem = "{\"gameTitle\":\"SampleProject\",\"versionId\":100,\"currencyUnit\":\"G\"}";
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", vanillaSystem)
        });

        MergePlan emptyPlan = Composer.Compose(
            Array.Empty<ModChangeSet>(),
            Array.Empty<Resolution>(),
            baseFiles);

        Assert.True(emptyPlan.IsDeployable);

        IReadOnlyList<MergedFile> withoutVersionId = MergedFileWriter.Write(emptyPlan, baseFiles, systemVersionId: null);
        Assert.Empty(withoutVersionId);

        IReadOnlyList<MergedFile> withVersionId = MergedFileWriter.Write(emptyPlan, baseFiles, systemVersionId: 777);
        MergedFile systemFile = Assert.Single(withVersionId);
        Assert.Equal("data/system.json", systemFile.File);
        Assert.Equal(MergedFileSource.Text, systemFile.Source);
        Assert.Equal("{\"gameTitle\":\"SampleProject\",\"versionId\":777,\"currencyUnit\":\"G\"}", systemFile.Text);
    }

    [Fact]
    public void Write_AllOpsDroppedByPickVanilla_OmitsTheFile()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50}]")
        });

        RecordKey key = new("data/items.json", "id:1");

        ModChangeSet csA = new(
            "a-mod",
            0,
            new[] { new ChangeOp("a-mod", key, "price", ChangeKind.SetLeaf, new JValue(60), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csB = new(
            "b-mod",
            1,
            new[] { new ChangeOp("b-mod", key, "price", ChangeKind.SetLeaf, new JValue(70), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan unresolved = Composer.Compose(new[] { csA, csB }, Array.Empty<Resolution>(), baseFiles);
        Collision collision = Assert.Single(unresolved.Collisions);

        MergePlan resolvedPlan = Composer.Compose(
            new[] { csA, csB },
            new[] { new Resolution(collision.Id, ResolutionKind.PickVanilla, null, collision.Fingerprint) },
            baseFiles);

        Assert.True(resolvedPlan.IsDeployable);
        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(resolvedPlan, baseFiles, systemVersionId: null);
        Assert.Empty(written);
    }

    [Fact]
    public void Write_RemovedRecords_AreBlankInsideAndCutAtTheEnd()
    {
        const string vanillaItems =
            "[\nnull,\n{\"id\":1,\"name\":\"Herb\",\"price\":50},\n{\"id\":2,\"name\":\"Tonic\",\"price\":100},\n{\"id\":3,\"name\":\"Salve\",\"price\":150}\n]";
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", vanillaItems)
        });

        ModChangeSet cs = new(
            "a-trim",
            0,
            new[]
            {
                new ChangeOp("a-trim", new RecordKey("data/items.json", "id:1"), "", ChangeKind.RemoveRecord, null, "data/Items.json"),
                new ChangeOp("a-trim", new RecordKey("data/items.json", "id:3"), "", ChangeKind.RemoveRecord, null, "data/Items.json")
            },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { cs }, Array.Empty<Resolution>(), baseFiles);
        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(plan, baseFiles, systemVersionId: null);

        MergedFile file = Assert.Single(written);
        Assert.Equal("data/items.json", file.File);
        // GR-R8: a hole above index 0 takes the blank record; the trailing removal is cut.
        Assert.Equal(
            "[null," + "{\"id\":1,\"animationId\":0,\"consumable\":true,\"damage\":{\"critical\":false,\"elementId\":0,\"formula\":\"0\",\"type\":0,\"variance\":20},\"description\":\"\",\"effects\":[],\"hitType\":0,\"iconIndex\":0,\"itypeId\":1,\"name\":\"\",\"note\":\"\",\"occasion\":0,\"price\":0,\"repeats\":1,\"scope\":7,\"speed\":0,\"successRate\":100,\"tpGain\":0}" + ",{\"id\":2,\"name\":\"Tonic\",\"price\":100}]",
            file.Text);
    }

    [Fact]
    public void Write_TouchedRecord_KeepsVanillaOrderAndAppendsNewProperties()
    {
        const string vanillaSystem = "{\"gameTitle\":\"Sample\",\"currencyUnit\":\"G\"}";
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/System.json", vanillaSystem)
        });

        RecordKey sysKey = new("data/system.json", "*");
        ModChangeSet cs = new(
            "a-order",
            0,
            new[]
            {
                new ChangeOp("a-order", sysKey, "customTag", ChangeKind.SetLeaf, new JValue("extra"), "data/System.json"),
                new ChangeOp("a-order", sysKey, "gameTitle", ChangeKind.SetLeaf, new JValue("Renamed"), "data/System.json")
            },
            Array.Empty<OperationDiagnostic>());

        MergePlan plan = Composer.Compose(new[] { cs }, Array.Empty<Resolution>(), baseFiles);
        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(plan, baseFiles, systemVersionId: null);

        MergedFile file = Assert.Single(written);
        Assert.Equal("{\"gameTitle\":\"Renamed\",\"currencyUnit\":\"G\",\"customTag\":\"extra\"}", file.Text);
    }

    private static BaseFile CreateBaseFile(string path, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new BaseFile(path, text, sha);
    }
}
