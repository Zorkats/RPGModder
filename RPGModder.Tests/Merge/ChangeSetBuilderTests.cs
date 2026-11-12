using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class ChangeSetBuilderTests
{
    private static string[] Ops(ModSource mod, BaseFileSet bases)
    {
        ModChangeSet set = Build(mod, bases);
        Assert.Empty(set.Diagnostics);
        return set.Ops.Select(Describe).ToArray();
    }

    [Fact]
    public void LeafEdit_IsOneSetLeafOnTheRecord()
    {
        string text = EditItems(items => items[2]!["price"] = 250);

        Assert.Equal(
            ["data/items.json|id:2|price|SetLeaf|250"],
            Ops(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems))));
    }

    [Fact]
    public void NewRecord_IsAddRecordWithTheWholeRecord()
    {
        string text = EditItems(items => items.Add(JObject.Parse("{\"id\":3,\"name\":\"Elixir\"}")));

        Assert.Equal(
            ["data/items.json|id:3||AddRecord|{\"id\":3,\"name\":\"Elixir\"}"],
            Ops(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems))));
    }

    [Fact]
    public void RecordSetToNullOrCutOff_IsRemoveRecord()
    {
        string nulled = EditItems(items => items[1] = JValue.CreateNull());
        string cut = EditItems(items => items.RemoveAt(2));
        BaseFileSet bases = Bases(Base(Items, VanillaItems));

        Assert.Equal(["data/items.json|id:1||RemoveRecord|-"], Ops(FileMod("a-mod", Items, nulled), bases));
        Assert.Equal(["data/items.json|id:2||RemoveRecord|-"], Ops(FileMod("a-mod", Items, cut), bases));
    }

    [Fact]
    public void SequenceArray_IsOneEditSequenceWithTheWholeList()
    {
        string text = EditItems(items => ((JArray)items[1]!["effects"]!).Add(JObject.Parse("{\"code\":21}")));

        string[] ops = Ops(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems)));

        Assert.Single(ops);
        Assert.StartsWith("data/items.json|id:1|effects|EditSequence|[", ops[0]);
    }

    [Fact]
    public void RemovedProperty_IsRemoveLeaf()
    {
        string text = EditItems(items => ((JObject)items[2]!).Remove("note"));

        Assert.Equal(
            ["data/items.json|id:2|note|RemoveLeaf|-"],
            Ops(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems))));
    }

    [Fact]
    public void PositionalArrays_AreComparedPerIndex_AndALengthChangeIsOneValue()
    {
        const string vanilla = "[null,{\"id\":1,\"params\":[[1,2,3],[4,5,6]],\"learnings\":[],\"traits\":[]}]";
        string edited = vanilla.Replace("[4,5,6]", "[4,9,6]");
        string resized = vanilla.Replace("[4,5,6]", "[4,5,6,7]");
        BaseFileSet bases = Bases(Base(Classes, vanilla));

        Assert.Equal(["data/classes.json|id:1|params[1][1]|SetLeaf|9"], Ops(FileMod("a-mod", Classes, edited), bases));
        Assert.Equal(["data/classes.json|id:1|params[1]|SetLeaf|[4,5,6,7]"], Ops(FileMod("a-mod", Classes, resized), bases));
    }

    [Fact]
    public void ArrayTheSchemaDoesNotName_IsAtomic()
    {
        const string vanilla = "[null,{\"id\":1,\"equips\":[1,1,2,3,0]}]";
        string edited = vanilla.Replace("[1,1,2,3,0]", "[1,1,2,3,4]");

        Assert.Equal(
            ["data/actors.json|id:1|equips|SetLeaf|[1,1,2,3,4]"],
            Ops(FileMod("a-mod", "data/Actors.json", edited), Bases(Base("data/Actors.json", vanilla))));
    }

    [Fact]
    public void CommandLists_AreOneEditCommands_InCommonEventsAndTroopPages()
    {
        const string events = "[null,{\"id\":1,\"name\":\"E\",\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}]}]";
        const string troops = "[null,{\"id\":1,\"members\":[{\"enemyId\":1}],\"pages\":[{\"span\":0,\"list\":[{\"code\":0}]}]}]";
        string eventsEdited = events.Replace("\"list\":[", "\"list\":[{\"code\":101},");
        string troopsEdited = troops.Replace("\"list\":[", "\"list\":[{\"code\":101},").Replace("\"enemyId\":1", "\"enemyId\":2");

        Assert.Equal(
            ["data/commonevents.json|id:1|list|EditCommands|[{\"code\":101},{\"code\":0,\"indent\":0,\"parameters\":[]}]"],
            Ops(FileMod("a-mod", "data/CommonEvents.json", eventsEdited), Bases(Base("data/CommonEvents.json", events))));
        Assert.Equal(
            [
                "data/troops.json|id:1|members|EditSequence|[{\"enemyId\":2}]",
                "data/troops.json|id:1|pages[0].list|EditCommands|[{\"code\":101},{\"code\":0}]"
            ],
            Ops(FileMod("a-mod", "data/Troops.json", troopsEdited), Bases(Base("data/Troops.json", troops))));
    }

    [Fact]
    public void SystemNameArrays_AreKeyedPerIndex_AndTermsArePositional()
    {
        string text = EditSystem(system =>
        {
            ((JArray)system["switches"]!).Add("Bridge");
            system["switches"]![1] = "Gate";
            system["terms"]!["basic"]![1] = "LV";
        });

        Assert.Equal(
            [
                "data/system.json|*|switches[1]|SetLeaf|\"Gate\"",
                "data/system.json|*|switches[3]|SetLeaf|\"Bridge\"",
                "data/system.json|*|terms.basic[1]|SetLeaf|\"LV\""
            ],
            Ops(FileMod("a-mod", SystemFile, text), Bases(Base(SystemFile, VanillaSystem))));
    }

    [Fact]
    public void ShortenedNameArray_RemovesTheMissingIndexes()
    {
        string text = EditSystem(system => ((JArray)system["switches"]!).RemoveAt(2));

        Assert.Equal(
            ["data/system.json|*|switches[2]|RemoveLeaf|-"],
            Ops(FileMod("a-mod", SystemFile, text), Bases(Base(SystemFile, VanillaSystem))));
    }

    [Fact]
    public void VolatileFields_GiveNoOps_EvenNestedAndThroughPatches()
    {
        string text = EditSystem(system =>
        {
            system["versionId"] = 99;
            system["editMapId"] = 7;
            system["screenWidth"] = 1280;
            system["advanced"] = null;
        });
        string withAdvanced = VanillaSystem.Replace("\"partyMembers\"", "\"advanced\":{\"screenWidth\":816,\"fontSize\":26},\"partyMembers\"");
        BaseFileSet advancedBases = Bases(Base(SystemFile, withAdvanced));

        Assert.Equal(
            ["data/system.json|*|advanced|SetLeaf|null"],
            Ops(FileMod("a-mod", SystemFile, text), Bases(Base(SystemFile, VanillaSystem))));
        Assert.Empty(Ops(PatchMod("a-mod", SystemFile, "{\"advanced\":{\"screenWidth\":1280},\"versionId\":3}"), advancedBases));
    }

    [Fact]
    public void MapInfosEditorState_GivesNoOps()
    {
        const string vanilla = "[null,{\"id\":1,\"expanded\":false,\"name\":\"Town\",\"scrollX\":0,\"scrollY\":0}]";
        const string edited = "[null,{\"id\":1,\"expanded\":true,\"name\":\"Town\",\"scrollX\":500,\"scrollY\":40}]";

        Assert.Empty(Ops(FileMod("a-mod", "data/MapInfos.json", edited), Bases(Base("data/MapInfos.json", vanilla))));
    }

    [Fact]
    public void UnknownDataFile_IsDetectedFromVanillaShape()
    {
        const string records = "[null,{\"id\":1,\"label\":\"x\",\"rows\":[1,2]}]";
        const string other = "[1,2,3]";
        BaseFileSet bases = Bases(Base("data/Quests.json", records), Base("data/Table.json", other));

        Assert.Equal(
            ["data/quests.json|id:1|rows|SetLeaf|[1,3]"],
            Ops(FileMod("a-mod", "data/Quests.json", records.Replace("[1,2]", "[1,3]")), bases));
        Assert.Equal(
            ["data/table.json|*||SetLeaf|[1,2,4]"],
            Ops(FileMod("a-mod", "data/Table.json", "[1,2,4]"), bases));
    }

    [Fact]
    public void RootOfAnotherType_InAFileTheTableDoesNotName_IsOneValueForTheWholeFile()
    {
        Assert.Equal(
            ["data/quests.json|*||SetLeaf|{\"quests\":[]}"],
            Ops(FileMod("a-mod", Quests, "{\"quests\":[]}"), Bases(Base(Quests, VanillaQuests))));
    }

    [Fact]
    public void PatchAndWholeFile_GiveTheSameOps()
    {
        string text = EditSystem(system =>
        {
            system["currencyUnit"] = "Coins";
            system["terms"]!["messages"]!["victory"] = "Done";
        });
        BaseFileSet bases = Bases(Base(SystemFile, VanillaSystem));

        ModChangeSet whole = Build(FileMod("a-mod", SystemFile, text), bases);
        ModChangeSet patch = Build(PatchMod("a-mod", SystemFile, "{\"currencyUnit\":\"Coins\",\"terms\":{\"messages\":{\"victory\":\"Done\"}}}"), bases);

        Assert.Equal(
            whole.Ops.Select(op => Describe(op)),
            patch.Ops.Select(op => Describe(op)));
        Assert.All(patch.Ops, op => Assert.Null(op.SourceFile));
        Assert.All(whole.Ops, op => Assert.Equal("files/System.json", op.SourceFile));
    }

    [Fact]
    public void PatchesApplyAfterTheModsOwnFile_InManifestOrder()
    {
        string text = EditSystem(system => system["currencyUnit"] = "Coins");
        var mod = new ModSource(
            "a-mod",
            0,
            [new ModFile(SystemFile, "System.json", text, Hash(text))],
            [
                new ModPatch(SystemFile, JObject.Parse("{\"gameTitle\":\"One\"}")),
                new ModPatch("DATA\\system.json", JObject.Parse("{\"gameTitle\":\"Two\"}"))
            ],
            []);

        Assert.Equal(
            [
                "data/system.json|*|currencyUnit|SetLeaf|\"Coins\"",
                "data/system.json|*|gameTitle|SetLeaf|\"Two\""
            ],
            Ops(mod, Bases(Base(SystemFile, VanillaSystem))));
    }

    [Fact]
    public void BrokenModFile_GivesParseFailedWithTheModId_AndNoOpForThatFile()
    {
        string good = EditSystem(system => system["currencyUnit"] = "Coins");
        var mod = new ModSource(
            "a-mod",
            0,
            [
                new ModFile(Items, "Items.json", "[null,{\"id\":1,", Hash("x")),
                new ModFile(SystemFile, "System.json", good, Hash(good))
            ],
            [],
            []);

        ModChangeSet set = Build(mod, Bases(Base(Items, VanillaItems), Base(SystemFile, VanillaSystem)));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("a-mod", diagnostic.Subject);
        Assert.Contains("Items.json", diagnostic.Message);
        Assert.Equal(["data/system.json|*|currencyUnit|SetLeaf|\"Coins\""], set.Ops.Select(Describe));
    }

    [Theory]
    [InlineData("[1,2] trailing")]
    [InlineData("")]
    public void TextAfterTheRootOrNoText_IsAParseFailure(string text)
    {
        ModChangeSet set = Build(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems)));

        Assert.Equal(DiagnosticCodes.MergeParseFailed, Assert.Single(set.Diagnostics).Code);
        Assert.Empty(set.Ops);
    }

    [Fact]
    public void BrokenBaseFile_GivesBaseParseFailedWithTheFile()
    {
        ModChangeSet set = Build(FileMod("a-mod", Items, VanillaItems), Bases(Base(Items, "{oops")));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeBaseParseFailed, diagnostic.Code);
        Assert.Equal("data/items.json", diagnostic.Subject);
        Assert.Empty(set.Ops);
    }

    [Fact]
    public void PatchOfAFileVanillaDoesNotHave_GivesPatchTargetMissing()
    {
        ModChangeSet set = Build(PatchMod("a-mod", "data/Missing.json", "{\"a\":1}"), Bases());

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergePatchTargetMissing, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("a-mod", diagnostic.Subject);
    }

    [Fact]
    public void PatchOfAnArrayFile_GivesPatchShapeMismatch()
    {
        ModChangeSet set = Build(PatchMod("a-mod", Items, "{\"1\":{\"price\":1}}"), Bases(Base(Items, VanillaItems)));

        Assert.Equal(DiagnosticCodes.MergePatchShapeMismatch, Assert.Single(set.Diagnostics).Code);
        Assert.Empty(set.Ops);
    }

    [Fact]
    public void DateLikeStrings_StayStrings()
    {
        string text = EditItems(items => items[1]!["note"] = "2024-01-02T03:04:05Z");

        ModChangeSet set = Build(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems)));

        Assert.Equal(JTokenType.String, Assert.Single(set.Ops).Value!.Type);
    }

    [Fact]
    public void SameInput_GivesEqualOutput()
    {
        string text = EditItems(items =>
        {
            items[1]!["price"] = 1;
            items.Add(JObject.Parse("{\"id\":3}"));
        });
        ModSource mod = FileMod("a-mod", Items, text);
        BaseFileSet bases = Bases(Base(Items, VanillaItems));

        Assert.Equal(
            ModChangeSetSerializer.Serialize(Build(mod, bases)),
            ModChangeSetSerializer.Serialize(Build(mod, bases)));
    }

    [Fact]
    public void OddPropertyNames_AreQuotedInThePath()
    {
        const string vanilla = "{\"a.b\":1,\"it's\":1}";
        const string edited = "{\"a.b\":2,\"it's\":2}";

        Assert.Equal(
            ["data/odd.json|*|['a.b']|SetLeaf|2", "data/odd.json|*|['it\\'s']|SetLeaf|2"],
            Ops(FileMod("a-mod", "data/Odd.json", edited), Bases(Base("data/Odd.json", vanilla))));
    }

    [Theory]
    [InlineData(Items, "{\"items\":[]}")]
    [InlineData(SystemFile, "[]")]
    [InlineData(SystemFile, "null")]
    public void ModRootOfAnotherType_InAFileTheGameReads_IsRootShapeMismatchOnTheMod(string file, string text)
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems), Base(SystemFile, VanillaSystem));

        ModChangeSet set = Build(FileMod("a-mod", file, text), bases);

        Assert.Empty(set.Ops);
        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("a-mod", diagnostic.Subject);
    }

    [Fact]
    public void GameRootOfAnotherType_IsRootShapeMismatchOnTheFile()
    {
        ModChangeSet set = Build(FileMod("a-mod", Items, VanillaItems), Bases(Base(Items, "{\"id\":1}")));

        Assert.Empty(set.Ops);
        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeRootShapeMismatch, diagnostic.Code);
        Assert.Equal("data/items.json", diagnostic.Subject);
    }

    [Theory]
    [InlineData("[null,{\"id\":1,\"price\":undefined}]")]
    [InlineData("[null,{\"id\":1,\"price\":new Date(5)}]")]
    [InlineData("[null,{\"id\":1,\"price\":NaN}]")]
    [InlineData("[null,{\"id\":1,\"price\":1e999}]")]
    [InlineData("[null,{'id':1}]")]
    [InlineData("[null,{id:1}]")]
    [InlineData("[null,{\"id\":1},]")]
    [InlineData("[null,{\"id\":0x10}]")]
    [InlineData("[null,{\"id\":1} /* note */]")]
    [InlineData("[null,{\"id\":1}] []")]
    public void TextTheGameCannotParse_IsParseFailed_WithNoOp(string text)
    {
        ModChangeSet set = Build(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems)));

        Assert.Empty(set.Ops);
        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diagnostic.Code);
        Assert.Contains("line 1, position", diagnostic.Message);
    }

    [Fact]
    public void LeadingByteOrderMark_IsRead()
    {
        string text = "\uFEFF" + EditItems(items => items[2]!["price"] = 250);

        Assert.Equal(
            ["data/items.json|id:2|price|SetLeaf|250"],
            Ops(FileMod("a-mod", Items, text), Bases(Base(Items, VanillaItems))));
    }

    [Fact]
    public void IdTooLargeForALong_InAFileTheTableDoesNotName_IsOneValue()
    {
        Assert.Equal(
            ["data/quests.json|*||SetLeaf|[{\"id\":1}]"],
            Ops(FileMod("a-mod", Quests, "[{\"id\":1}]"), Bases(Base(Quests, "[{\"id\":99999999999999999999}]"))));
    }

    [Fact]
    public void FileOpsNamingOneTargetTwice_TheLaterEntryWins()
    {
        string first = EditItems(items => items[2]!["price"] = 1);
        string second = EditItems(items => items[2]!["price"] = 2);
        var mod = new ModSource(
            "a-mod",
            0,
            [new ModFile(Items, "files/first.json", first, Hash(first)), new ModFile(Items, "files/second.json", second, Hash(second))],
            [],
            []);

        ChangeOp op = Assert.Single(Build(mod, Bases(Base(Items, VanillaItems))).Ops);

        Assert.Equal("data/items.json|id:2|price|SetLeaf|2", Describe(op));
        Assert.Equal("files/second.json", op.SourceFile);
    }

    [Theory]
    [InlineData("data/Tilesets.json", "flags", "[0,16,15]", "[0,16,9]", "flags[2]|SetLeaf|9")]
    [InlineData("data/Tilesets.json", "tilesetNames", "[\"A\",\"B\"]", "[\"A\",\"C\"]", "tilesetNames|SetLeaf|[\"A\",\"C\"]")]
    [InlineData("data/Enemies.json", "dropItems", "[{\"kind\":0},{\"kind\":1}]", "[{\"kind\":0},{\"kind\":2}]", "dropItems[1].kind|SetLeaf|2")]
    [InlineData("data/Enemies.json", "actions", "[{\"skillId\":1}]", "[{\"skillId\":1},{\"skillId\":2}]", "actions|EditSequence|[{\"skillId\":1},{\"skillId\":2}]")]
    [InlineData("data/Classes.json", "learnings", "[{\"level\":1,\"skillId\":3}]", "[]", "learnings|EditSequence|[]")]
    public void RecordFileArrays_FollowTheSchemaTable(string file, string field, string before, string after, string expected)
    {
        string vanilla = $"[null,{{\"id\":1,\"{field}\":{before}}}]";
        string modded = $"[null,{{\"id\":1,\"{field}\":{after}}}]";

        Assert.Equal(
            [$"{MergePath.ToKey(file)}|id:1|{expected}"],
            Ops(FileMod("a-mod", file, modded), Bases(Base(file, vanilla))));
    }

    [Theory]
    [InlineData("{\"sounds\":[{\"name\":\"A\"},{\"name\":\"B\"}]}", "{\"sounds\":[{\"name\":\"A\"},{\"name\":\"C\"}]}", "sounds[1].name|SetLeaf|\"C\"")]
    [InlineData("{\"terms\":{\"commands\":[\"Fight\",\"Escape\"]}}", "{\"terms\":{\"commands\":[\"Fight\",\"Run\"]}}", "terms.commands[1]|SetLeaf|\"Run\"")]
    [InlineData("{\"terms\":{\"params\":[\"HP\",\"MP\"]}}", "{\"terms\":{\"params\":[\"HP\",\"SP\"]}}", "terms.params[1]|SetLeaf|\"SP\"")]
    [InlineData("{\"elements\":[\"\",\"Fire\"]}", "{\"elements\":[\"\",\"Fire\",\"Ice\"]}", "elements[2]|SetLeaf|\"Ice\"")]
    [InlineData("{\"partyMembers\":[1]}", "{\"partyMembers\":[1,2]}", "partyMembers|SetLeaf|[1,2]")]
    public void SystemArrays_FollowTheSchemaTable(string vanilla, string modded, string expected)
    {
        Assert.Equal(
            [$"data/system.json|*|{expected}"],
            Ops(FileMod("a-mod", SystemFile, modded), Bases(Base(SystemFile, vanilla))));
    }
}
