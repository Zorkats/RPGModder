using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class OpaqueAndNewDataFileContractTests
{
    [Fact]
    public void Build_OpaqueFile_GivesNoOpWhenEqualToVanillaAndReplaceFileWhenDifferent()
    {
        const string vanillaHash = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        const string modHash = "ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100";

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            new BaseFile("img/pictures/title.png", null, vanillaHash),
            new BaseFile("img/pictures/hud.png", null, vanillaHash)
        });

        ModSource mod = new(
            "a-opaque",
            0,
            new[]
            {
                new ModFile("img/pictures/title.png", "assets/title.png", null, vanillaHash),
                new ModFile("img/pictures/hud.png", "assets/hud.png", null, modHash)
            },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Empty(changeSet.Diagnostics);
        ChangeOp op = Assert.Single(changeSet.Ops);
        Assert.Equal(new RecordKey("img/pictures/hud.png", "*"), op.Key);
        Assert.Equal("", op.Path);
        Assert.Equal(ChangeKind.ReplaceFile, op.Kind);
        Assert.Equal(modHash, op.Value!.Value<string>());
        Assert.Equal("assets/hud.png", op.SourceFile);
    }

    [Fact]
    public void Compose_OpaqueFiles_MergesEqualHashesAndReportsFileClashOnDifferentHashes()
    {
        const string sharedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string clashHashA = "1111111111111111111111111111111111111111111111111111111111111111";
        const string clashHashB = "2222222222222222222222222222222222222222222222222222222222222222";

        BaseFileSet baseFiles = BaseFileSet.From(Array.Empty<BaseFile>());

        ModChangeSet csEqualA = ChangeSetBuilder.Build(
            new ModSource(
                "a-ash",
                0,
                new[] { new ModFile("js/plugins/Shared.js", "files/a_shared.js", null, sharedHash) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ModChangeSet csEqualB = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { new ModFile("js/plugins/Shared.js", "files/b_shared.js", null, sharedHash) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        MergePlan equalPlan = Composer.Compose(new[] { csEqualB, csEqualA }, Array.Empty<Resolution>(), baseFiles);

        Assert.True(equalPlan.IsDeployable);
        Assert.Empty(equalPlan.Collisions);
        MergedOp mergedOp = Assert.Single(equalPlan.Ops);
        Assert.Equal(new[] { "a-ash", "b-birch" }, mergedOp.ModIds);

        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(equalPlan, baseFiles, systemVersionId: null);
        MergedFile file = Assert.Single(written);
        Assert.Equal("js/plugins/shared.js", file.File);
        Assert.Equal(MergedFileSource.ModFile, file.Source);
        Assert.Equal("a-ash", file.ModId);
        Assert.Equal("files/a_shared.js", file.SourceFile);

        ModChangeSet csClashA = ChangeSetBuilder.Build(
            new ModSource(
                "a-ash",
                0,
                new[] { new ModFile("js/plugins/Shared.js", "files/a_shared.js", null, clashHashA) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ModChangeSet csClashB = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { new ModFile("js/plugins/Shared.js", "files/b_shared.js", null, clashHashB) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        MergePlan clashPlan = Composer.Compose(new[] { csClashA, csClashB }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(clashPlan.IsDeployable);
        Collision clash = Assert.Single(clashPlan.Collisions);
        Assert.Equal(CollisionKind.FileClash, clash.Kind);
        Assert.Equal(new RecordKey("js/plugins/shared.js", "*"), clash.Key);
        Assert.Equal("", clash.Path);
        Assert.True(clash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod }, clash.Allowed);
    }

    [Fact]
    public void BuildAndCompose_NewDataFile_GivesAddRecordOnStarAndHandlesWhitespaceEqualityAndIdClash()
    {
        BaseFileSet baseFiles = BaseFileSet.From(Array.Empty<BaseFile>());

        const string compactJson = "[null,{\"id\":1,\"name\":\"Relic\",\"price\":300}]";
        const string prettyJson = "[\n  null,\n  {\n    \"id\": 1,\n    \"name\": \"Relic\",\n    \"price\": 300\n  }\n]";
        const string differentJson = "[null,{\"id\":1,\"name\":\"Relic\",\"price\":450}]";

        ModChangeSet csA = ChangeSetBuilder.Build(
            new ModSource(
                "a-ash",
                0,
                new[] { CreateModFile("data/CustomRelics.json", "custom/a_relics.json", compactJson) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        ModChangeSet csBEqual = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { CreateModFile("data/CustomRelics.json", "custom/b_relics.json", prettyJson) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        Assert.Empty(csA.Diagnostics);
        ChangeOp opA = Assert.Single(csA.Ops);
        Assert.Equal(new RecordKey("data/customrelics.json", "*"), opA.Key);
        Assert.Equal("", opA.Path);
        Assert.Equal(ChangeKind.AddRecord, opA.Kind);
        Assert.Equal("custom/a_relics.json", opA.SourceFile);
        Assert.True(JToken.DeepEquals(JArray.Parse(compactJson), opA.Value));

        MergePlan equalPlan = Composer.Compose(new[] { csBEqual, csA }, Array.Empty<Resolution>(), baseFiles);

        Assert.True(equalPlan.IsDeployable);
        Assert.Empty(equalPlan.Collisions);
        MergedOp mergedOp = Assert.Single(equalPlan.Ops);
        Assert.Equal(new[] { "a-ash", "b-birch" }, mergedOp.ModIds);

        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(equalPlan, baseFiles, systemVersionId: null);
        MergedFile file = Assert.Single(written);
        Assert.Equal("data/customrelics.json", file.File);
        Assert.Equal(MergedFileSource.ModFile, file.Source);
        Assert.Equal("a-ash", file.ModId);
        Assert.Equal("custom/a_relics.json", file.SourceFile);

        ModChangeSet csBDiff = ChangeSetBuilder.Build(
            new ModSource(
                "b-birch",
                1,
                new[] { CreateModFile("data/CustomRelics.json", "custom/b_relics.json", differentJson) },
                Array.Empty<ModPatch>(),
                Array.Empty<PluginEntry>()),
            baseFiles);

        MergePlan clashPlan = Composer.Compose(new[] { csA, csBDiff }, Array.Empty<Resolution>(), baseFiles);

        Assert.False(clashPlan.IsDeployable);
        Collision clash = Assert.Single(clashPlan.Collisions);
        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.Equal(new RecordKey("data/customrelics.json", "*"), clash.Key);
        Assert.Equal("", clash.Path);
        Assert.True(clash.Blocking);
        Assert.Equal(new[] { ResolutionKind.PickMod, ResolutionKind.DisableMod }, clash.Allowed);
    }

    private static ModFile CreateModFile(string target, string source, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ModFile(target, source, text, sha);
    }
}
