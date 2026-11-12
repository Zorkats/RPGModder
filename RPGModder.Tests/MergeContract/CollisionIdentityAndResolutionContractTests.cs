using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class CollisionIdentityAndResolutionContractTests
{
    [Fact]
    public void Collision_ValuesChange_KeepsIdAndChangesFingerprint()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50},{\"id\":2,\"name\":\"Tonic\",\"price\":120}]")
        });

        RecordKey key = new("data/items.json", "id:2");

        ModChangeSet csTinV1 = new(
            "a-tin",
            0,
            new[] { new ChangeOp("a-tin", key, "price", ChangeKind.SetLeaf, new JValue(80), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csOakV1 = new(
            "b-oak",
            1,
            new[] { new ChangeOp("b-oak", key, "price", ChangeKind.SetLeaf, new JValue(150), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        ModChangeSet csTinV2 = new(
            "a-tin",
            0,
            new[] { new ChangeOp("a-tin", key, "price", ChangeKind.SetLeaf, new JValue(95), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csOakV2 = new(
            "b-oak",
            1,
            new[] { new ChangeOp("b-oak", key, "price", ChangeKind.SetLeaf, new JValue(175), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan planV1 = Composer.Compose(new[] { csTinV1, csOakV1 }, Array.Empty<Resolution>(), baseFiles);
        MergePlan planV2 = Composer.Compose(new[] { csTinV2, csOakV2 }, Array.Empty<Resolution>(), baseFiles);

        Collision c1 = Assert.Single(planV1.Collisions);
        Collision c2 = Assert.Single(planV2.Collisions);

        Assert.Equal("6405909de5ee88d1", c1.Id);
        Assert.Equal(c1.Id, c2.Id);
        Assert.NotEmpty(c1.Fingerprint);
        Assert.NotEmpty(c2.Fingerprint);
        Assert.NotEqual(c1.Fingerprint, c2.Fingerprint);
    }

    [Fact]
    public void Compose_StaleFingerprint_ReusesTheChoiceWithAnInfoDiagnostic()
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("data/Items.json", "[null,{\"id\":1,\"name\":\"Herb\",\"price\":50},{\"id\":2,\"name\":\"Tonic\",\"price\":120}]")
        });

        RecordKey key = new("data/items.json", "id:2");

        ModChangeSet csTinV1 = new(
            "a-tin",
            0,
            new[] { new ChangeOp("a-tin", key, "price", ChangeKind.SetLeaf, new JValue(80), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());
        ModChangeSet csOakV1 = new(
            "b-oak",
            1,
            new[] { new ChangeOp("b-oak", key, "price", ChangeKind.SetLeaf, new JValue(150), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan initialPlan = Composer.Compose(new[] { csTinV1, csOakV1 }, Array.Empty<Resolution>(), baseFiles);
        Collision initialCollision = Assert.Single(initialPlan.Collisions);

        Resolution storedResolution = new(
            initialCollision.Id,
            ResolutionKind.PickMod,
            "b-oak",
            initialCollision.Fingerprint);

        MergePlan matchingPlan = Composer.Compose(new[] { csTinV1, csOakV1 }, new[] { storedResolution }, baseFiles);
        Assert.True(matchingPlan.IsDeployable);
        CollisionOutcome matchingOutcome = Assert.Single(matchingPlan.Outcomes);
        Assert.Equal(ResolutionSource.Profile, matchingOutcome.Source);
        Assert.Equal(ResolutionKind.PickMod, matchingOutcome.Kind);
        Assert.Equal("b-oak", matchingOutcome.ModId);
        Assert.DoesNotContain(matchingPlan.Diagnostics, d => d.Code == DiagnosticCodes.RulesResolutionReused);

        ModChangeSet csOakV2 = new(
            "b-oak",
            1,
            new[] { new ChangeOp("b-oak", key, "price", ChangeKind.SetLeaf, new JValue(190), "data/Items.json") },
            Array.Empty<OperationDiagnostic>());

        MergePlan updatedPlan = Composer.Compose(new[] { csTinV1, csOakV2 }, new[] { storedResolution }, baseFiles);

        Assert.True(updatedPlan.IsDeployable);
        CollisionOutcome reusedOutcome = Assert.Single(updatedPlan.Outcomes);
        Assert.Equal(initialCollision.Id, reusedOutcome.CollisionId);
        Assert.Equal(ResolutionSource.ProfileReused, reusedOutcome.Source);
        Assert.Equal(ResolutionKind.PickMod, reusedOutcome.Kind);
        Assert.Equal("b-oak", reusedOutcome.ModId);

        OperationDiagnostic reusedDiag = Assert.Single(
            updatedPlan.Diagnostics,
            d => d.Code == DiagnosticCodes.RulesResolutionReused);
        Assert.Equal(DiagnosticSeverity.Info, reusedDiag.Severity);

        MergedOp winningOp = Assert.Single(updatedPlan.Ops);
        Assert.Equal(190, winningOp.Op.Value!.Value<int>());
    }

    private static BaseFile CreateBaseFile(string path, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new BaseFile(path, text, sha);
    }
}
