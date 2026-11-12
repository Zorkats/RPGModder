using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class MergedFileWriterTests
{
    private static IReadOnlyList<MergedFile> Write(BaseFileSet bases, int? versionId, params ModSource[] mods)
    {
        MergePlan plan = Compose(bases, mods);
        Assert.True(plan.IsDeployable);
        return MergedFileWriter.Write(plan, bases, versionId);
    }

    [Fact]
    public void TouchedFile_IsCompactWithVanillaOrder_AndNewPropertiesLast()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems), Base(SystemFile, VanillaSystem));
        ModSource mod = FileMod("a-tin", Items, EditItems(items =>
        {
            items[1]!["price"] = 60;
            items[1]!["iconIndex"] = 176;
        }));

        MergedFile file = Assert.Single(Write(bases, null, mod));

        Assert.Equal("data/items.json", file.File);
        Assert.Equal(MergedFileSource.Text, file.Source);
        Assert.Equal(["a-tin"], file.ModIds);
        Assert.Null(file.DiskPath);
        Assert.Equal(
            "[null,{\"id\":1,\"name\":\"Tonic\",\"price\":60,\"note\":\"\",\"effects\":[{\"code\":11,\"dataId\":0,\"value1\":0,\"value2\":100}],\"iconIndex\":176}," +
            "{\"id\":2,\"name\":\"Ether\",\"price\":200,\"note\":\"\",\"effects\":[]}]",
            file.Text);
    }

    [Fact]
    public void RemovedRecords_BecomeBlank_AndATrailingRunIsCut()
    {
        const string vanilla = "[null,{\"id\":1},{\"id\":2},{\"id\":3}]";
        BaseFileSet bases = Bases(Base("data/Weapons.json", vanilla));

        MergedFile middle = Assert.Single(Write(bases, null, FileMod("a-tin", "data/Weapons.json", "[null,{\"id\":1},null,{\"id\":3}]")));
        MergedFile tail = Assert.Single(Write(bases, null, FileMod("a-tin", "data/Weapons.json", "[null,{\"id\":1}]")));

        // GR-R8: the hole takes the blank weapon with its own id.
        Assert.Equal("[null,{\"id\":1},{\"id\":2,\"animationId\":0,\"description\":\"\",\"etypeId\":1,\"traits\":[{\"code\":31,\"dataId\":1,\"value\":0},{\"code\":22,\"dataId\":0,\"value\":0}],\"iconIndex\":0,\"name\":\"\",\"note\":\"\",\"params\":[0,0,0,0,0,0,0,0],\"price\":0,\"wtypeId\":0},{\"id\":3}]", middle.Text);
        Assert.Equal("[null,{\"id\":1}]", tail.Text);
    }

    [Fact]
    public void NewRecordsFromTwoMods_AreBothWritten_EvenPastAGap()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource first = FileMod("a-tin", Items, EditItems(items => items.Add(new JObject { ["id"] = 3 })));
        ModSource second = FileMod("b-oak", Items, EditItems(items =>
        {
            items.Add(JValue.CreateNull());
            items.Add(new JObject { ["id"] = 4 });
        }));

        MergedFile file = Assert.Single(Write(bases, null, first, second));

        Assert.EndsWith("{\"id\":3},{\"id\":4}]", file.Text);
        Assert.Equal(["a-tin", "b-oak"], file.ModIds);
    }

    [Fact]
    public void KeyedNames_GrowAndShrinkFromTheEnd()
    {
        BaseFileSet bases = Bases(Base(SystemFile, VanillaSystem));
        ModSource grow = FileMod("a-tin", SystemFile, EditSystem(system =>
        {
            ((JArray)system["switches"]!).Add("Bridge");
            ((JArray)system["switches"]!).Add("Tower");
        }));
        ModSource shrink = FileMod("a-tin", SystemFile, EditSystem(system => system["switches"] = new JArray("")));

        JObject grown = JObject.Parse(Assert.Single(Write(bases, null, grow)).Text!);
        JObject shrunk = JObject.Parse(Assert.Single(Write(bases, null, shrink)).Text!);

        Assert.Equal(new JArray("", "Door", "Lever", "Bridge", "Tower"), grown["switches"]);
        Assert.Equal(new JArray(""), shrunk["switches"]);
    }

    [Fact]
    public void WholeFileValue_ReplacesTheRoot()
    {
        MergedFile file = Assert.Single(Write(Bases(Base(Quests, VanillaQuests)), null, FileMod("a-tin", Quests, "{ \"x\" : 1 }")));

        Assert.Equal("{\"x\":1}", file.Text);
    }

    [Fact]
    public void RemovedLeaf_IsGone()
    {
        MergedFile file = Assert.Single(Write(
            Bases(Base(Items, VanillaItems)),
            null,
            FileMod("a-tin", Items, EditItems(items => ((JObject)items[2]!).Remove("note")))));

        Assert.Contains("{\"id\":2,\"name\":\"Ether\",\"price\":200,\"effects\":[]}", file.Text);
    }

    [Fact]
    public void VersionId_ListsSystemEvenWhenNoModTouchedIt()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems), Base(SystemFile, VanillaSystem));

        IReadOnlyList<MergedFile> files = Write(bases, 123456, FileMod("a-tin", Items, EditItems(items => items[1]!["price"] = 1)));

        Assert.Equal(["data/items.json", "data/system.json"], files.Select(file => file.File));
        MergedFile system = files[1];
        Assert.Empty(system.ModIds);
        Assert.Equal(VanillaSystem.Replace("\"versionId\":11", "\"versionId\":123456"), system.Text);
    }

    [Fact]
    public void WithoutVersionId_SystemKeepsVanillasVersionId()
    {
        BaseFileSet bases = Bases(Base(SystemFile, VanillaSystem));

        MergedFile system = Assert.Single(Write(bases, null, FileMod("a-tin", SystemFile, EditSystem(item => item["currencyUnit"] = "C"))));

        Assert.Contains("\"versionId\":11", system.Text);
        Assert.Empty(Write(Bases(Base(Items, VanillaItems), Base(SystemFile, VanillaSystem)), null));
    }

    [Fact]
    public void VersionIdWithoutSystemBase_IsACallerBug()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        MergePlan plan = Compose(bases, FileMod("a-tin", Items, EditItems(items => items[1]!["price"] = 1)));

        Assert.Throws<ArgumentException>(() => MergedFileWriter.Write(plan, bases, 5));
    }

    [Fact]
    public void FileWhoseOpsWereAllDropped_IsNotListed()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource first = FileMod("a-tin", Items, EditItems(items => items[2]!["price"] = 1));
        ModSource second = FileMod("b-oak", Items, EditItems(items => items[2]!["price"] = 2));
        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickVanilla, null)], first, second);

        Assert.Empty(MergedFileWriter.Write(plan, bases, null));
    }

    [Fact]
    public void PlanThatIsNotDeployable_Throws()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        MergePlan plan = Compose(
            bases,
            FileMod("a-tin", Items, EditItems(items => items[2]!["price"] = 1)),
            FileMod("b-oak", Items, EditItems(items => items[2]!["price"] = 2)));

        Assert.Throws<InvalidOperationException>(() => MergedFileWriter.Write(plan, bases, null));
    }

    [Fact]
    public void EqualPlans_GiveEqualBytes()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource mod = FileMod("a-tin", Items, EditItems(items => items[1]!["note"] = "héllo \"quoted\""));

        string first = Assert.Single(Write(bases, null, mod)).Text!;
        string second = Assert.Single(Write(bases, null, mod)).Text!;

        Assert.Equal(first, second);
        Assert.Contains("\"note\":\"héllo \\\"quoted\\\"\"", first);
    }

    [Fact]
    public void KeyedList_ShrinkingPastTen_CutsInNumberOrder()
    {
        var names = new JArray(Enumerable.Range(0, 12).Select(index => (object)("S" + index)).ToArray());
        string vanilla = EditSystem(system => system["switches"] = names);
        BaseFileSet bases = Bases(Base(SystemFile, vanilla));
        ModSource shrink = FileMod("a-tin", SystemFile, EditSystem(system => system["switches"] = new JArray(names.Take(8))));

        JObject written = JObject.Parse(Assert.Single(Write(bases, null, shrink)).Text!);

        Assert.Equal(new JArray(names.Take(8)), written["switches"]);
    }

    [Fact]
    public void KeyedList_TailCutByOneModAndGrownByAnother_LeavesAHole()
    {
        BaseFileSet bases = Bases(Base(SystemFile, VanillaSystem));
        ModSource cut = FileMod("a-tin", SystemFile, EditSystem(system => system["switches"] = new JArray("", "Door")));
        ModSource grow = FileMod("b-oak", SystemFile, EditSystem(system => ((JArray)system["switches"]!).Add("Bridge")));

        JObject written = JObject.Parse(Assert.Single(Write(bases, null, cut, grow)).Text!);

        Assert.Equal(new JArray("", "Door", JValue.CreateNull(), "Bridge"), written["switches"]);
    }

    [Fact]
    public void VersionId_WithAModChangeToSystem_CarriesBoth()
    {
        BaseFileSet bases = Bases(Base(SystemFile, VanillaSystem));

        MergedFile system = Assert.Single(Write(bases, 77, FileMod("a-tin", SystemFile, EditSystem(item => item["currencyUnit"] = "C"))));

        Assert.Equal(VanillaSystem.Replace("\"versionId\":11", "\"versionId\":77").Replace("\"currencyUnit\":\"G\"", "\"currencyUnit\":\"C\""), system.Text);
        Assert.Equal(["a-tin"], system.ModIds);
    }

    [Fact]
    public void RemovedRecordBeforeAnAddedOne_BecomesBlank()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource removal = FileMod("a-tin", Items, EditItems(items => items[2] = JValue.CreateNull()));
        ModSource addition = FileMod("b-oak", Items, EditItems(items => items.Add(new JObject { ["id"] = 3 })));

        JArray written = JArray.Parse(Assert.Single(Write(bases, null, removal, addition)).Text!);

        Assert.Equal(4, written.Count);
        Assert.Equal(2, written[2]["id"]!.Value<int>());
        Assert.Equal("", written[2]["name"]!.Value<string>());
        Assert.Equal(new JObject { ["id"] = 3 }, written[3]);
    }

    [Fact]
    public void DeleteVersusEdit_KeptRecord_IsWrittenWithEveryEdit()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource[] mods =
        [
            FileMod("a-tin", Items, EditItems(items => items[2] = JValue.CreateNull())),
            FileMod("b-oak", Items, EditItems(items => items[2]!["price"] = 9)),
            FileMod("c-elm", Items, EditItems(items => items[2]!["name"] = "Mist"))
        ];
        Collision collision = Assert.Single(Compose(bases, mods).Collisions);
        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "c-elm")], mods);

        MergedFile file = Assert.Single(MergedFileWriter.Write(plan, bases, null));

        Assert.Contains("{\"id\":2,\"name\":\"Mist\",\"price\":9,\"note\":\"\",\"effects\":[]}", file.Text);
        Assert.Equal(["b-oak", "c-elm"], file.ModIds);
    }
}
