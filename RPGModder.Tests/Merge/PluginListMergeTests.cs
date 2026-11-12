using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class PluginListMergeTests
{
    private const string AlphaParameters = "{\"speed\":\"1\",\"Font Size\":\"20\"}";

    private static readonly string Alpha = Plugin("Alpha", AlphaParameters);
    private static readonly string Omega = Plugin("Omega");

    private static string WriteText(BaseFileSet bases, MergePlan plan)
    {
        MergedFile file = Assert.Single(MergedFileWriter.Write(plan, bases, null));
        Assert.Equal(MergedFileSource.Text, file.Source);
        return file.Text!;
    }

    [Fact]
    public void Build_ParameterEdits_AreLeavesOnThePluginRecord()
    {
        ModSource mod = FileMod("a-tin", PluginList, PluginsJs(Plugin("Alpha", "{\"speed\":\"2\",\"Font Size\":\"24\"}"), Omega));

        ModChangeSet set = Build(mod, Bases(Base(PluginList, VanillaPlugins)));

        Assert.Empty(set.Diagnostics);
        Assert.Equal(
            [
                "js/plugins.js|plugin:Alpha|parameters.speed|SetLeaf|\"2\"",
                "js/plugins.js|plugin:Alpha|parameters['Font Size']|SetLeaf|\"24\""
            ],
            set.Ops.Select(Describe).Order(StringComparer.Ordinal));
        Assert.All(set.Ops, op => Assert.Equal("files/plugins.js", op.SourceFile));
    }

    [Fact]
    public void Build_RegistryAdd_IsARecordAndAnAnchorWithNoSourceFile()
    {
        ModChangeSet set = Build(RegistryMod("a-tin", 0, Entry("Gamma", ("x", "1"))), Bases(Base(PluginList, VanillaPlugins)));

        Assert.Equal(
            [
                "js/plugins.js|plugin:Gamma||AddRecord|{\"name\":\"Gamma\",\"status\":true,\"description\":\"\",\"parameters\":{\"x\":\"1\"}}",
                "js/plugins.js|plugin:Gamma|@after|SetLeaf|\"Omega\""
            ],
            set.Ops.Select(Describe));
        Assert.All(set.Ops, op => Assert.Null(op.SourceFile));
    }

    [Fact]
    public void Write_RegistryAdd_AppendsInTheEditorLayout()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));

        string text = WriteText(bases, Compose(bases, RegistryMod("a-tin", 0, Entry("Gamma", ("x", "1")))));

        Assert.Equal(PluginsJs(Alpha, Omega, Plugin("Gamma", "{\"x\":\"1\"}")), text);
    }

    [Fact]
    public void Build_RegistryEntryForAListedName_ReplacesItWhereItIs()
    {
        ModChangeSet set = Build(RegistryMod("a-tin", 0, Entry("Alpha", ("speed", "3"))), Bases(Base(PluginList, VanillaPlugins)));

        Assert.Equal(
            [
                "js/plugins.js|plugin:Alpha|parameters.speed|SetLeaf|\"3\"",
                "js/plugins.js|plugin:Alpha|parameters['Font Size']|RemoveLeaf|-"
            ],
            set.Ops.Select(Describe).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_Swap_MovesTheEntryVanillaHadFirst()
    {
        ModChangeSet set = Build(FileMod("a-tin", PluginList, PluginsJs(Omega, Alpha)), Bases(Base(PluginList, VanillaPlugins)));

        Assert.Equal(["js/plugins.js|plugin:Alpha|@after|SetLeaf|\"Omega\""], set.Ops.Select(Describe));
    }

    [Theory]
    [InlineData("V0,V4,V5,V1,V2,V3")]
    [InlineData("V2,V3,V4,V0,V1,V5")]
    [InlineData("V5,V4,V3,V2,V1,V0")]
    public void Write_BlockMoveInOneMod_KeepsTheModsOrder(string order)
    {
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(Names("V0,V1,V2,V3,V4,V5"))));
        ModSource mod = FileMod("a-tin", PluginList, PluginsJs(Names(order)));

        Assert.Equal(PluginsJs(Names(order)), WriteText(bases, Compose(bases, mod)));
    }

    [Fact]
    public void Write_EveryOrderOfOneMod_IsWrittenAsTheModListsIt()
    {
        string[] vanilla = ["A", "B", "C", "D", "E"];
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(vanilla.Select(name => Plugin(name)).ToArray())));

        // E is removed and N is new; a new entry first in a mod's list goes to the end (GR-C10), so N never leads.
        foreach (List<string> order in Permutations(["A", "B", "C", "D", "N"]).Where(order => order[0] != "N"))
        {
            string text = PluginsJs(order.Select(name => Plugin(name)).ToArray());

            Assert.Equal(text, WriteText(bases, Compose(bases, FileMod("a-tin", PluginList, text))));
        }
    }

    [Fact]
    public void Compose_OneModsRunOfNewEntries_IsNotSplitByAnotherMod()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        ModSource zinc = RegistryMod("a-zinc", 0, Entry("Zinc_Core"), Entry("Zinc_Battle"));
        ModSource fig = RegistryMod("b-fig", 1, Entry("Fig"));

        MergePlan plan = Compose(bases, zinc, fig);

        Assert.Equal(CollisionKind.OrderAmbiguous, Assert.Single(plan.Collisions).Kind);
        Assert.Equal(
            PluginsJs(Alpha, Omega, Plugin("Zinc_Core"), Plugin("Zinc_Battle"), Plugin("Fig")),
            WriteText(bases, plan));
    }

    [Fact]
    public void Build_RegistryEntryWithNoName_IsAnErrorAndTheOthersStillMerge()
    {
        ModSource mod = RegistryMod("a-tin", 0, Entry(""), Entry("Gamma"));

        ModChangeSet set = Build(mod, Bases(Base(PluginList, VanillaPlugins)));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diagnostic.Code);
        Assert.Equal("a-tin", diagnostic.Subject);
        Assert.All(set.Ops, op => Assert.Equal("plugin:Gamma", op.Key.Record));
    }

    [Fact]
    public void Write_NameMentionedInACommentFirst_FindsTheDeclaration()
    {
        string vanilla = "// The $plugins list of this game.\n" + VanillaPlugins;
        BaseFileSet bases = Bases(Base(PluginList, vanilla));

        string text = WriteText(bases, Compose(bases, RegistryMod("a-tin", 0, Entry("Gamma"))));

        Assert.Equal("// The $plugins list of this game.\n" + PluginsJs(Alpha, Omega, Plugin("Gamma")), text);
    }

    private static string[] Names(string order)
    {
        return order.Split(',').Select(name => Plugin(name)).ToArray();
    }

    private static IEnumerable<List<string>> Permutations(List<string> items)
    {
        if (items.Count <= 1)
        {
            yield return [.. items];
            yield break;
        }

        foreach (string item in items)
        {
            foreach (List<string> rest in Permutations(items.Where(other => other != item).ToList()))
            {
                rest.Insert(0, item);
                yield return rest;
            }
        }
    }

    [Fact]
    public void Write_Swap_PutsTheMovedFirstEntryInFront()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));

        string text = WriteText(bases, Compose(bases, FileMod("a-tin", PluginList, PluginsJs(Omega, Alpha))));

        Assert.Equal(PluginsJs(Omega, Alpha), text);
    }

    [Fact]
    public void Build_Removal_DoesNotMoveTheNeighbour()
    {
        ModChangeSet set = Build(FileMod("a-tin", PluginList, PluginsJs(Omega)), Bases(Base(PluginList, VanillaPlugins)));

        Assert.Equal(["js/plugins.js|plugin:Alpha||RemoveRecord|-"], set.Ops.Select(Describe));
    }

    [Fact]
    public void Write_Insertion_LandsAfterItsAnchorAndMovesNothingElse()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        ModSource mod = FileMod("a-tin", PluginList, PluginsJs(Alpha, Plugin("Beta"), Omega));

        ModChangeSet set = Build(mod, bases);
        string text = WriteText(bases, Compose(bases, mod));

        Assert.Equal(2, set.Ops.Count);
        Assert.Equal(PluginsJs(Alpha, Plugin("Beta"), Omega), text);
    }

    [Fact]
    public void Write_RepeatedNames_ReplaceTheWholeList()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        ModSource mod = FileMod("a-tin", PluginList, PluginsJs(Omega, Omega));

        ChangeOp op = Assert.Single(Build(mod, bases).Ops);
        string text = WriteText(bases, Compose(bases, mod));

        Assert.Equal("*|", op.Key.Record + "|" + op.Path);
        Assert.Equal(PluginsJs(Omega, Omega), text);
    }

    [Fact]
    public void Write_EveryEntryRemoved_IsAnEmptyList()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));

        string text = WriteText(bases, Compose(bases, FileMod("a-tin", PluginList, PluginsJs())));

        Assert.Equal(PluginsJs(), text);
    }

    [Fact]
    public void Write_NoVanillaPluginList_UsesTheEditorPrefix()
    {
        BaseFileSet bases = Bases();

        string text = WriteText(bases, Compose(bases, RegistryMod("a-tin", 0, Entry("Gamma"))));

        Assert.Equal(PluginsJs(Plugin("Gamma")), text);
    }

    [Fact]
    public void Write_CrlfVanilla_KeepsCrlf()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins.Replace("\n", "\r\n", StringComparison.Ordinal)));

        string text = WriteText(bases, Compose(bases, RegistryMod("a-tin", 0, Entry("Gamma"))));

        Assert.Equal(PluginsJs(Alpha, Omega, Plugin("Gamma")).Replace("\n", "\r\n", StringComparison.Ordinal), text);
    }

    [Theory]
    [InlineData("var $other = [];\n")]
    [InlineData("var $plugins = {};\n")]
    [InlineData("var $plugins = [];\nalert(1);\n")]
    [InlineData("var $plugins = [{\"name\":\"A\"}\n")]
    public void Build_ModListThatIsNotTheEditorForm_IsAParseFailure(string text)
    {
        ModChangeSet set = Build(FileMod("a-tin", PluginList, text), Bases(Base(PluginList, VanillaPlugins)));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diagnostic.Code);
        Assert.Equal("a-tin", diagnostic.Subject);
    }

    [Fact]
    public void Build_BrokenVanillaList_IsABaseFaultOnTheFile()
    {
        ModChangeSet set = Build(RegistryMod("a-tin", 0, Entry("Gamma")), Bases(Base(PluginList, "window.$plugins = 1;")));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeBaseParseFailed, diagnostic.Code);
        Assert.Equal(PluginList, diagnostic.Subject);
    }

    [Fact]
    public void Build_PatchOnThePluginList_IsRejected()
    {
        ModChangeSet set = Build(PatchMod("a-tin", PluginList, "{\"Alpha\":{}}"), Bases(Base(PluginList, VanillaPlugins)));

        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergePatchShapeMismatch, Assert.Single(set.Diagnostics).Code);
    }

    [Theory]
    [InlineData(0, 1, "Omega")]
    [InlineData(1, 0, "Alpha")]
    public void Compose_OneNewEntryAtTwoPlaces_DefaultsToTheLatestModInProfileOrder(int tinPriority, int oakPriority, string winner)
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        string gamma = Plugin("Gamma");
        ModSource tin = FileMod("a-tin", PluginList, PluginsJs(Alpha, gamma, Omega), tinPriority);
        ModSource oak = FileMod("b-oak", PluginList, PluginsJs(Alpha, Omega, gamma), oakPriority);

        MergePlan plan = Compose(bases, tin, oak);

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.PluginOrderClash, collision.Kind);
        Assert.Equal("plugin:Gamma|@after", collision.Key.Record + "|" + collision.Path);
        Assert.False(collision.Blocking);
        Assert.Equal(ResolutionSource.Default, Assert.Single(plan.Outcomes).Source);
        string expected = winner == "Omega" ? PluginsJs(Alpha, Omega, gamma) : PluginsJs(Alpha, gamma, Omega);
        Assert.Equal(expected, WriteText(bases, plan));
    }

    [Fact]
    public void Compose_OneNewEntryAtTwoPlaces_PickModTakesThatModsPlace()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        string gamma = Plugin("Gamma");
        ModSource tin = FileMod("a-tin", PluginList, PluginsJs(Alpha, gamma, Omega), 0);
        ModSource oak = FileMod("b-oak", PluginList, PluginsJs(Alpha, Omega, gamma), 1);
        Collision collision = Assert.Single(Compose(bases, tin, oak).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "a-tin", collision.Fingerprint)], tin, oak);

        Assert.Equal(ResolutionSource.Profile, Assert.Single(plan.Outcomes).Source);
        Assert.Equal(PluginsJs(Alpha, gamma, Omega), WriteText(bases, plan));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, false)]
    public void Compose_TwoNewEntriesAfterOneAnchor_DefaultToProfileOrder(int ashPriority, int birchPriority, bool ashFirst)
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        string ash = Plugin("Ash");
        string birch = Plugin("Birch");
        ModSource first = FileMod("a-ash", PluginList, PluginsJs(Alpha, Omega, ash), ashPriority);
        ModSource second = FileMod("b-birch", PluginList, PluginsJs(Alpha, Omega, birch), birchPriority);

        MergePlan plan = Compose(bases, first, second);

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.OrderAmbiguous, collision.Kind);
        Assert.Equal("plugin:Omega|@after", collision.Key.Record + "|" + collision.Path);
        Assert.Equal(["a-ash", "b-birch"], collision.ModIds);
        Assert.False(collision.Blocking);
        Assert.Equal(ashFirst ? PluginsJs(Alpha, Omega, ash, birch) : PluginsJs(Alpha, Omega, birch, ash), WriteText(bases, plan));
    }

    [Fact]
    public void Compose_TwoNewEntriesAfterOneAnchor_PickModPutsThatModFirst()
    {
        BaseFileSet bases = Bases(Base(PluginList, VanillaPlugins));
        string ash = Plugin("Ash");
        string birch = Plugin("Birch");
        ModSource first = FileMod("a-ash", PluginList, PluginsJs(Alpha, Omega, ash), 0);
        ModSource second = FileMod("b-birch", PluginList, PluginsJs(Alpha, Omega, birch), 1);
        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "b-birch", collision.Fingerprint)], first, second);

        Assert.Equal(PluginsJs(Alpha, Omega, birch, ash), WriteText(bases, plan));
    }

    [Theory]
    [InlineData(0, 1, "V2,V3,V0,V1")]
    [InlineData(1, 0, "V3,V2,V0,V1")]
    public void Compose_TwoModsMovingEntriesToTheFront_IsOrderAmbiguousInProfileOrder(int ashPriority, int birchPriority, string expected)
    {
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(Names("V0,V1,V2,V3"))));
        ModSource ash = FileMod("a-ash", PluginList, PluginsJs(Names("V2,V0,V1,V3")), ashPriority);
        ModSource birch = FileMod("b-birch", PluginList, PluginsJs(Names("V3,V0,V1,V2")), birchPriority);

        MergePlan plan = Compose(bases, ash, birch);

        Collision collision = Assert.Single(plan.Collisions);
        Assert.Equal(CollisionKind.OrderAmbiguous, collision.Kind);
        Assert.Equal("*|@first", collision.Key.Record + "|" + collision.Path);
        Assert.Equal(["a-ash", "b-birch"], collision.ModIds);
        Assert.Equal(PluginsJs(Names(expected)), WriteText(bases, plan));
    }

    [Fact]
    public void Compose_TwoModsMovingEntriesToTheFront_PickModPutsThatModFirst()
    {
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(Names("V0,V1,V2,V3"))));
        ModSource ash = FileMod("a-ash", PluginList, PluginsJs(Names("V2,V0,V1,V3")), 0);
        ModSource birch = FileMod("b-birch", PluginList, PluginsJs(Names("V3,V0,V1,V2")), 1);
        Collision collision = Assert.Single(Compose(bases, ash, birch).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "b-birch", collision.Fingerprint)], ash, birch);

        Assert.Equal(PluginsJs(Names("V3,V2,V0,V1")), WriteText(bases, plan));
    }

    [Fact]
    public void Compose_TwoModsMovingTheSameEntryToTheFront_IsNoCollision()
    {
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(Names("V0,V1,V2,V3"))));
        string moved = PluginsJs(Names("V2,V0,V1,V3"));

        MergePlan plan = Compose(bases, FileMod("a-ash", PluginList, moved, 0), FileMod("b-birch", PluginList, moved, 1));

        Assert.Empty(plan.Collisions);
        Assert.Equal(moved, WriteText(bases, plan));
    }

    [Fact]
    public void Compose_ModsMovingAnEntryToTheFrontAndAddingOneAtTheEnd_AreTwoSeparatePlaces()
    {
        // Both anchors are null: the moved V2 goes to the front, the new N (first in its mod's list) to the end.
        BaseFileSet bases = Bases(Base(PluginList, PluginsJs(Names("V0,V1,V2,V3"))));
        ModSource ash = FileMod("a-ash", PluginList, PluginsJs(Names("V2,V0,V1,V3")), 0);
        ModSource birch = FileMod("b-birch", PluginList, PluginsJs(Names("N,V0,V1,V2,V3")), 1);

        MergePlan plan = Compose(bases, ash, birch);

        Assert.Empty(plan.Collisions);
        Assert.Equal(PluginsJs(Names("V2,V0,V1,V3,N")), WriteText(bases, plan));
    }

    [Fact]
    public void Place_AnchorCycle_GoesToTheEndByName()
    {
        List<string> order = PluginPlacement.Place(
            ["A", "B"],
            new HashSet<string> { "A", "B", "Y", "X" },
            new HashSet<string> { "X", "Y" },
            new Dictionary<string, string?> { ["X"] = "Y", ["Y"] = "X" });

        Assert.Equal(["A", "B", "X", "Y"], order);
    }

    [Fact]
    public void Place_AnchorOnARemovedVanillaEntry_FallsBackToTheNearestEarlierOne()
    {
        List<string> order = PluginPlacement.Place(
            ["A", "B", "C"],
            new HashSet<string> { "A", "C", "X" },
            new HashSet<string> { "X" },
            new Dictionary<string, string?> { ["X"] = "B" });

        Assert.Equal(["A", "X", "C"], order);
    }

    [Fact]
    public void Place_AnchorThatIsNowhere_GoesToTheEnd()
    {
        List<string> order = PluginPlacement.Place(
            ["A", "B"],
            new HashSet<string> { "A", "B", "X" },
            new HashSet<string> { "X" },
            new Dictionary<string, string?> { ["X"] = "Zed" });

        Assert.Equal(["A", "B", "X"], order);
    }

    [Fact]
    public void Place_MovedEntriesWithNoAnchor_GoFirstAndNewOnesLast()
    {
        List<string> order = PluginPlacement.Place(
            ["A", "B", "C"],
            new HashSet<string> { "A", "B", "C", "N" },
            new HashSet<string> { "N" },
            new Dictionary<string, string?> { ["C"] = null, ["N"] = null });

        Assert.Equal(["C", "A", "B", "N"], order);
    }

    [Fact]
    public void Place_TwoEntriesBehindOneAnchor_KeepTheirPlacingOrder()
    {
        List<string> order = PluginPlacement.Place(
            ["A", "B"],
            new HashSet<string> { "A", "B", "X", "Y" },
            new HashSet<string> { "X", "Y" },
            new Dictionary<string, string?> { ["X"] = "A", ["Y"] = "A" });

        Assert.Equal(["A", "X", "Y", "B"], order);
    }
}
