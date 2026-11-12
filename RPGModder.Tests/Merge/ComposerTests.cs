using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Security.Cryptography;
using System.Text;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class ComposerTests
{
    private static readonly BaseFileSet ItemBases = Bases(Base(Items, VanillaItems));

    private static ModSource Price(string modId, int itemId, int price)
    {
        return FileMod(modId, Items, EditItems(items => items[itemId]!["price"] = price));
    }

    private static ModSource Name(string modId, int itemId, string name)
    {
        return FileMod(modId, Items, EditItems(items => items[itemId]!["name"] = name));
    }

    private static ModSource Removal(string modId, int itemId)
    {
        return FileMod(modId, Items, EditItems(items => items[itemId] = JValue.CreateNull()));
    }

    private static string[] OpsOf(MergePlan plan)
    {
        return plan.Ops.Select(merged => Describe(merged.Op)).ToArray();
    }

    [Fact]
    public void DifferentFieldsOfOneRecord_BothApply()
    {
        MergePlan plan = Compose(
            ItemBases,
            Price("a-tin", 1, 60),
            FileMod("b-oak", Items, EditItems(items => items[1]!["name"] = "Big Tonic")));

        Assert.True(plan.IsDeployable);
        Assert.Empty(plan.Collisions);
        Assert.Equal(
            ["data/items.json|id:1|name|SetLeaf|\"Big Tonic\"", "data/items.json|id:1|price|SetLeaf|60"],
            plan.Ops.Select(merged => Describe(merged.Op)));
    }

    [Fact]
    public void EqualEdits_AreOneOpWithEveryMod()
    {
        MergePlan plan = Compose(ItemBases, Price("b-oak", 1, 60), Price("a-tin", 1, 60));

        MergedOp merged = Assert.Single(plan.Ops);
        Assert.Equal(["a-tin", "b-oak"], merged.ModIds);
        Assert.Equal("a-tin", merged.Op.ModId);
        Assert.Empty(plan.Collisions);
    }

    [Fact]
    public void DifferentValues_AreABlockingValueClash()
    {
        MergePlan plan = Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2));

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.ValueClash, collision.Kind);
        Assert.Equal(new RecordKey("data/items.json", "id:2"), collision.Key);
        Assert.Equal("price", collision.Path);
        Assert.Equal(["a-tin", "b-oak"], collision.ModIds);
        Assert.True(collision.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.PickVanilla], collision.Allowed);
        Assert.Equal(ResolutionSource.Unresolved, Assert.Single(plan.Outcomes).Source);
        Assert.False(plan.IsDeployable);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void CollisionId_IsTheDocumentedHash()
    {
        MergePlan plan = Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2));

        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "ValueClash\ndata/items.json\nid:2\nprice\na-tin\nb-oak")))[..16];
        Assert.Equal(expected, Assert.Single(plan.Collisions).Id);
    }

    [Fact]
    public void CollisionId_IgnoresValues_ButFingerprintDoesNot()
    {
        Collision first = Assert.Single(Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2)).Collisions);
        Collision second = Assert.Single(Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 3)).Collisions);

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void RemoveAgainstEdit_IsDeleteVersusEditOnTheRecord()
    {
        MergePlan plan = Compose(
            ItemBases,
            FileMod("a-tin", Items, EditItems(items => items[2] = JValue.CreateNull())),
            Price("b-oak", 2, 9));

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.DeleteVersusEdit, collision.Kind);
        Assert.Equal("id:2", collision.Key.Record);
        Assert.Equal("", collision.Path);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.PickVanilla], collision.Allowed);
    }

    [Fact]
    public void DifferentNewRecordsAtOneId_AreAnIdClash_AndEqualOnesAreOneOp()
    {
        ModSource Add(string modId, string name)
        {
            return FileMod(modId, Items, EditItems(items => items.Add(new JObject { ["id"] = 3, ["name"] = name })));
        }

        MergePlan clash = Compose(ItemBases, Add("a-tin", "Rope"), Add("b-oak", "Lamp"));
        MergePlan same = Compose(ItemBases, Add("a-tin", "Rope"), Add("b-oak", "Rope"));

        Collision collision = Assert.Single(clash.Collisions);
        Assert.Equal(CollisionKind.IdClash, collision.Kind);
        Assert.True(collision.Blocking);
        Assert.Equal([ResolutionKind.PickMod, ResolutionKind.DisableMod], collision.Allowed);
        Assert.Empty(same.Collisions);
        Assert.Equal(["a-tin", "b-oak"], Assert.Single(same.Ops).ModIds);
    }

    [Fact]
    public void DifferentSequences_AreASequenceClash_AndDifferentCommandLists_ALogicClash()
    {
        ModSource Effects(string modId, int code)
        {
            return FileMod(modId, Items, EditItems(items => items[2]!["effects"] = new JArray(new JObject { ["code"] = code })));
        }

        const string events = "[null,{\"id\":1,\"list\":[{\"code\":0}]}]";
        BaseFileSet eventBases = Bases(Base("data/CommonEvents.json", events));

        Collision sequence = Assert.Single(Compose(ItemBases, Effects("a-tin", 11), Effects("b-oak", 12)).Collisions);
        Collision logic = Assert.Single(Compose(
            eventBases,
            FileMod("a-tin", "data/CommonEvents.json", events.Replace("[{", "[{\"code\":101},{")),
            FileMod("b-oak", "data/CommonEvents.json", events.Replace("[{", "[{\"code\":102},{"))).Collisions);

        Assert.Equal(CollisionKind.SequenceClash, sequence.Kind);
        Assert.Equal(CollisionKind.LogicClash, logic.Kind);
        Assert.True(sequence.Blocking);
        Assert.True(logic.Blocking);
    }

    [Fact]
    public void PathInsideAnotherModsValue_IsAValueClashOnTheShorterPath()
    {
        const string vanilla = "[null,{\"id\":1,\"params\":[[1,2,3],[4,5,6]]}]";
        BaseFileSet bases = Bases(Base(Classes, vanilla));

        MergePlan plan = Compose(
            bases,
            FileMod("a-tin", Classes, vanilla.Replace("[4,5,6]", "[4,5,6,7]")),
            FileMod("b-oak", Classes, vanilla.Replace("[4,5,6]", "[4,9,6]")));

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.ValueClash, collision.Kind);
        Assert.Equal("params[1]", collision.Path);
        Assert.Equal(["a-tin", "b-oak"], collision.ModIds);
    }

    [Fact]
    public void WholeFileValue_ClashesWithAnyRecordEdit()
    {
        BaseFileSet bases = Bases(Base(Quests, VanillaQuests));
        MergePlan plan = Compose(
            bases,
            FileMod("a-tin", Quests, "{\"x\":1}"),
            FileMod("b-oak", Quests, EditArray(VanillaQuests, quests => quests[1]!["reward"] = 2)));

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(new RecordKey("data/quests.json", "*"), collision.Key);
        Assert.Equal("", collision.Path);
    }

    [Fact]
    public void PickMod_KeepsThatModsOps_AndRecordsTheOutcome()
    {
        MergePlan open = Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2));
        Collision collision = Assert.Single(open.Collisions);

        MergePlan plan = Compose(
            ItemBases,
            [new Resolution(collision.Id, ResolutionKind.PickMod, "b-oak", collision.Fingerprint)],
            Price("a-tin", 2, 1),
            Price("b-oak", 2, 2));

        Assert.True(plan.IsDeployable);
        Assert.Equal(["data/items.json|id:2|price|SetLeaf|2"], plan.Ops.Select(merged => Describe(merged.Op)));
        Assert.Equal(new CollisionOutcome(collision.Id, ResolutionSource.Profile, ResolutionKind.PickMod, "b-oak"), Assert.Single(plan.Outcomes));
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void PickVanilla_DropsEveryOpOfTheCollision()
    {
        Collision collision = Assert.Single(Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2)).Collisions);

        MergePlan plan = Compose(
            ItemBases,
            [new Resolution(collision.Id, ResolutionKind.PickVanilla, null, collision.Fingerprint)],
            Price("a-tin", 2, 1),
            Price("b-oak", 2, 2));

        Assert.True(plan.IsDeployable);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void StaleFingerprint_ReusesTheChoice_WithAnInfoDiagnostic()
    {
        Collision old = Assert.Single(Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2)).Collisions);

        MergePlan plan = Compose(
            ItemBases,
            [new Resolution(old.Id, ResolutionKind.PickMod, "a-tin", old.Fingerprint)],
            Price("a-tin", 2, 5),
            Price("b-oak", 2, 2));

        Assert.Equal(ResolutionSource.ProfileReused, Assert.Single(plan.Outcomes).Source);
        OperationDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal(DiagnosticCodes.RulesResolutionReused, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(old.Id, diagnostic.Subject);
        Assert.True(plan.IsDeployable);
        Assert.Equal(["data/items.json|id:2|price|SetLeaf|5"], plan.Ops.Select(merged => Describe(merged.Op)));
    }

    [Theory]
    [InlineData(ResolutionKind.PickMod, "c-elm")]
    [InlineData(ResolutionKind.PickMod, null)]
    [InlineData(ResolutionKind.DisableMod, "a-tin")]
    [InlineData(ResolutionKind.Accept, null)]
    public void ChoiceThatDoesNotFit_LeavesTheCollisionUnresolved(ResolutionKind kind, string? modId)
    {
        Collision collision = Assert.Single(Compose(ItemBases, Price("a-tin", 2, 1), Price("b-oak", 2, 2)).Collisions);

        MergePlan plan = Compose(ItemBases, [new Resolution(collision.Id, kind, modId)], Price("a-tin", 2, 1), Price("b-oak", 2, 2));

        Assert.Equal(ResolutionSource.Unresolved, Assert.Single(plan.Outcomes).Source);
        Assert.False(plan.IsDeployable);
    }

    [Fact]
    public void ErrorDiagnostic_MakesThePlanNotDeployable()
    {
        MergePlan plan = Compose(ItemBases, FileMod("a-tin", Items, "{broken"));

        Assert.False(plan.IsDeployable);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, Assert.Single(plan.Diagnostics).Code);
    }

    [Fact]
    public void ShuffledInputs_GiveTheSamePlan()
    {
        ModSource[] mods =
        [
            Price("a-tin", 2, 1),
            Price("b-oak", 2, 2),
            FileMod("c-elm", Items, EditItems(items => items[1]!["name"] = "Draught")),
            Price("d-ash", 1, 70)
        ];
        List<ModChangeSet> sets = mods.Select(mod => Build(mod, ItemBases)).ToList();
        Collision collision = Assert.Single(Composer.Compose(sets, [], ItemBases).Collisions);
        Resolution[] resolutions =
        [
            new Resolution(collision.Id, ResolutionKind.PickMod, "b-oak"),
            new Resolution(collision.Id, ResolutionKind.PickMod, "a-tin"),
            new Resolution("0000000000000000", ResolutionKind.PickVanilla, null)
        ];

        MergePlan expected = Composer.Compose(sets, resolutions, ItemBases);
        var random = new Random(20261111);
        for (int round = 0; round < 20; round++)
        {
            List<ModChangeSet> shuffledSets = sets.OrderBy(_ => random.Next()).ToList();
            List<Resolution> shuffledResolutions = resolutions.OrderBy(_ => random.Next()).ToList();

            MergePlan plan = Composer.Compose(shuffledSets, shuffledResolutions, ItemBases);

            Assert.Equal(expected.MergeHash, plan.MergeHash);
            Assert.Equal(expected.Collisions.Select(item => item.Id), plan.Collisions.Select(item => item.Id));
            Assert.Equal(expected.Outcomes, plan.Outcomes);
            Assert.Equal(expected.Ops.Select(merged => Describe(merged.Op)), plan.Ops.Select(merged => Describe(merged.Op)));
        }

        Assert.Equal("a-tin", Assert.Single(expected.Outcomes).ModId);
        Assert.Matches("^[0-9a-f]{64}$", expected.MergeHash);
    }

    [Fact]
    public void MergeHash_ChangesWithTheOps()
    {
        Assert.NotEqual(
            Compose(ItemBases, Price("a-tin", 2, 1)).MergeHash,
            Compose(ItemBases, Price("a-tin", 2, 3)).MergeHash);
    }

    [Fact]
    public void TwoChangeSetsWithOneModId_AreACallerBug()
    {
        ModChangeSet set = Build(Price("a-tin", 2, 1), ItemBases);

        Assert.Throws<ArgumentException>(() => Composer.Compose([set, set], [], ItemBases));
    }

    [Fact]
    public void DeleteVersusEdit_PickingAnEditor_KeepsTheRecordWithEveryEdit()
    {
        ModSource[] mods = [Removal("a-tin", 2), Price("b-oak", 2, 9), Name("c-elm", 2, "Mist")];
        Collision collision = Assert.Single(Compose(ItemBases, mods).Collisions);

        MergePlan plan = Compose(ItemBases, [new Resolution(collision.Id, ResolutionKind.PickMod, "b-oak")], mods);

        Assert.Equal(["a-tin", "b-oak", "c-elm"], collision.ModIds);
        Assert.True(plan.IsDeployable);
        Assert.Equal(["data/items.json|id:2|name|SetLeaf|\"Mist\"", "data/items.json|id:2|price|SetLeaf|9"], OpsOf(plan));
    }

    [Fact]
    public void DeleteVersusEdit_PickingTheRemover_RemovesTheRecord()
    {
        ModSource[] mods = [Removal("a-tin", 2), Price("b-oak", 2, 9), Name("c-elm", 2, "Mist")];
        Collision collision = Assert.Single(Compose(ItemBases, mods).Collisions);

        MergePlan plan = Compose(ItemBases, [new Resolution(collision.Id, ResolutionKind.PickMod, "a-tin")], mods);

        Assert.True(plan.IsDeployable);
        Assert.Equal(["data/items.json|id:2||RemoveRecord|-"], OpsOf(plan));
    }

    [Fact]
    public void DeleteVersusEdit_KeptRecord_ReportsTheEditorsOwnClash()
    {
        ModSource[] mods = [Removal("a-tin", 2), Price("b-oak", 2, 9), Price("c-elm", 2, 8)];
        Collision outer = Assert.Single(Compose(ItemBases, mods).Collisions);
        var keep = new Resolution(outer.Id, ResolutionKind.PickMod, "c-elm");

        MergePlan kept = Compose(ItemBases, [keep], mods);
        Collision inner = Assert.Single(kept.Collisions, collision => collision.Id != outer.Id);
        MergePlan done = Compose(ItemBases, [keep, new Resolution(inner.Id, ResolutionKind.PickMod, "b-oak")], mods);

        Assert.False(kept.IsDeployable);
        Assert.Equal(CollisionKind.ValueClash, inner.Kind);
        Assert.Equal("price", inner.Path);
        Assert.Equal(["b-oak", "c-elm"], inner.ModIds);
        Assert.True(done.IsDeployable);
        Assert.Equal(2, done.Outcomes.Count);
        Assert.Equal(["data/items.json|id:2|price|SetLeaf|9"], OpsOf(done));
    }

    [Fact]
    public void WholeFileValue_PickingARecordEditor_KeepsEveryRecordEdit()
    {
        BaseFileSet bases = Bases(Base(Quests, VanillaQuests));
        ModSource[] mods =
        [
            FileMod("a-tin", Quests, "{\"x\":1}"),
            FileMod("b-oak", Quests, EditArray(VanillaQuests, quests => quests[1]!["reward"] = 6)),
            FileMod("c-elm", Quests, EditArray(VanillaQuests, quests => quests[2]!["name"] = "Carry"))
        ];
        Collision collision = Assert.Single(Compose(bases, mods).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "c-elm")], mods);

        Assert.True(plan.IsDeployable);
        Assert.Equal(["data/quests.json|id:1|reward|SetLeaf|6", "data/quests.json|id:2|name|SetLeaf|\"Carry\""], OpsOf(plan));
    }

    [Fact]
    public void ShuffledInputs_WithNestedCollisionsAndDiagnostics_GiveTheSamePlan()
    {
        ModSource[] mods =
        [
            Removal("a-tin", 2),
            Price("b-oak", 2, 9),
            Price("c-elm", 2, 8),
            Name("d-ash", 1, "Draught"),
            FileMod("e-fir", Items, "{broken"),
            PatchMod("f-yew", "data/Nothing.json", "{\"a\":1}")
        ];
        List<ModChangeSet> sets = mods.Select(mod => Build(mod, ItemBases)).ToList();
        Collision outer = Assert.Single(Composer.Compose(sets, [], ItemBases).Collisions);
        var keep = new Resolution(outer.Id, ResolutionKind.PickMod, "c-elm");
        Collision inner = Assert.Single(Composer.Compose(sets, [keep], ItemBases).Collisions, collision => collision.Id != outer.Id);
        Resolution[] resolutions = [keep, new Resolution(inner.Id, ResolutionKind.PickMod, "b-oak")];

        MergePlan expected = Composer.Compose(sets, resolutions, ItemBases);
        var random = new Random(20261112);
        for (int round = 0; round < 20; round++)
        {
            MergePlan plan = Composer.Compose(
                sets.OrderBy(_ => random.Next()).ToList(),
                resolutions.OrderBy(_ => random.Next()).ToList(),
                ItemBases);

            Assert.Equal(expected.MergeHash, plan.MergeHash);
            Assert.Equal(expected.Collisions, plan.Collisions, CollisionsEqual);
            Assert.Equal(expected.Outcomes, plan.Outcomes);
            Assert.Equal(expected.Diagnostics, plan.Diagnostics);
            Assert.Equal(OpsOf(expected), OpsOf(plan));
        }

        Assert.Equal(
            [DiagnosticCodes.MergeParseFailed, DiagnosticCodes.MergePatchTargetMissing],
            expected.Diagnostics.Select(diagnostic => diagnostic.Code));
    }

    [Fact]
    public void MergeHash_CoversTheOutcomes()
    {
        ModSource[] mods = [Price("a-tin", 2, 1), Price("b-oak", 2, 2)];
        Collision collision = Assert.Single(Compose(ItemBases, mods).Collisions);

        MergePlan open = Compose(ItemBases, mods);
        MergePlan vanilla = Compose(ItemBases, [new Resolution(collision.Id, ResolutionKind.PickVanilla, null)], mods);

        Assert.Empty(open.Ops);
        Assert.Empty(vanilla.Ops);
        Assert.NotEqual(open.MergeHash, vanilla.MergeHash);
    }

    [Fact]
    public void ChoiceWithoutAFingerprint_IsTakenAsMade()
    {
        ModSource[] mods = [Price("a-tin", 2, 1), Price("b-oak", 2, 2)];
        Collision collision = Assert.Single(Compose(ItemBases, mods).Collisions);

        MergePlan plan = Compose(ItemBases, [new Resolution(collision.Id, ResolutionKind.PickMod, "b-oak")], mods);

        Assert.Equal(ResolutionSource.Profile, Assert.Single(plan.Outcomes).Source);
        Assert.Empty(plan.Diagnostics);
    }

    [Fact]
    public void Diagnostics_AreFoldedAndSortedByCode()
    {
        BaseFileSet bases = Bases(Base(Items, "{broken"), Base(Classes, "[null]"));

        MergePlan plan = Compose(
            bases,
            FileMod("b-oak", Items, VanillaItems),
            FileMod("c-elm", Classes, "{broken"),
            FileMod("a-tin", Items, VanillaItems));

        Assert.Equal(
            [DiagnosticCodes.MergeBaseParseFailed, DiagnosticCodes.MergeParseFailed],
            plan.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal("data/items.json", plan.Diagnostics[0].Subject);
    }

    [Theory]
    [InlineData("{broken", DiagnosticCodes.MergeBaseParseFailed)]
    [InlineData("[]", DiagnosticCodes.MergeRootShapeMismatch)]
    public void BrokenSystemAmongTheBases_MakesThePlanNotDeployable(string systemText, string code)
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems), Base(SystemFile, systemText));

        MergePlan plan = Compose(bases, Price("a-tin", 2, 1));

        Assert.False(plan.IsDeployable);
        OperationDiagnostic diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal("data/system.json", diagnostic.Subject);
    }

    [Fact]
    public void ManyOps_AreComposedWithoutAScanPerOp()
    {
        const int records = 10_000;
        var first = new List<ChangeOp>();
        var second = new List<ChangeOp>();
        for (int index = 1; index <= records; index++)
        {
            var key = new RecordKey("data/items.json", "id:" + index);
            first.Add(new ChangeOp("a-tin", key, "price", ChangeKind.SetLeaf, new JValue(index), null));
            second.Add(new ChangeOp("b-oak", key, "name", ChangeKind.SetLeaf, new JValue("n" + index), null));
        }

        ModChangeSet[] sets = [new ModChangeSet("a-tin", 0, first, []), new ModChangeSet("b-oak", 1, second, [])];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MergePlan plan = Composer.Compose(sets, [], ItemBases);
        watch.Stop();

        Assert.Equal(2 * records, plan.Ops.Count);
        Assert.Empty(plan.Collisions);

        // About 0.3 s on a desktop today. A pairwise scan is 2 x 10^8 path checks here, so the loose bound still catches one.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Compose took {watch.Elapsed}.");
    }

    private static bool CollisionsEqual(Collision left, Collision right)
    {
        return left.Id == right.Id &&
               left.Kind == right.Kind &&
               left.Key == right.Key &&
               left.Path == right.Path &&
               left.Fingerprint == right.Fingerprint &&
               left.ModIds.SequenceEqual(right.ModIds) &&
               left.Allowed.SequenceEqual(right.Allowed);
    }
}
