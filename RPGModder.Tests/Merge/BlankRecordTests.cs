using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Services;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

/// <summary>TST-15, step 2: filler (GR-R7) and blank records (GR-R8). Content is invented (TST-2).</summary>
public sealed class BlankRecordTests
{
    /// <summary>
    /// The record the RPG Maker MZ 1.10.0 editor wrote for each database tab when its maximum was raised by one, in a
    /// project made from the default new project, with <c>id</c> set to 0 and the editor's key order.
    /// </summary>
    private static readonly Dictionary<string, string> MzEditorRecords = new(StringComparer.Ordinal)
    {
        ["actors.json"] =
            """{"id":0,"battlerName":"","characterIndex":0,"characterName":"","classId":1,"equips":[0,0,0,0,0],"faceIndex":0,"faceName":"","traits":[],"initialLevel":1,"maxLevel":99,"name":"","nickname":"","note":"","profile":""}""",
        ["animations.json"] =
            """{"id":0,"displayType":0,"effectName":"","flashTimings":[],"name":"","offsetX":0,"offsetY":0,"rotation":{"x":0,"y":0,"z":0},"scale":100,"soundTimings":[],"speed":100,"alignBottom":false}""",
        ["armors.json"] =
            """{"id":0,"atypeId":0,"description":"","etypeId":2,"traits":[{"code":22,"dataId":1,"value":0}],"iconIndex":0,"name":"","note":"","params":[0,0,0,0,0,0,0,0],"price":0}""",
        ["classes.json"] =
            """{"id":0,"expParams":[30,20,30,30],"traits":[{"code":23,"dataId":0,"value":1},{"code":22,"dataId":0,"value":0.95},{"code":22,"dataId":1,"value":0.05},{"code":22,"dataId":2,"value":0.04},{"code":41,"dataId":1,"value":0},{"code":51,"dataId":1,"value":0},{"code":52,"dataId":1,"value":0}],"learnings":[],"name":"","note":"","params":[[1,450,500,550,600,650,700,750,800,850,900,950,1000,1050,1100,1150,1200,1250,1300,1350,1400,1450,1500,1550,1600,1650,1700,1750,1800,1850,1900,1950,2000,2050,2100,2150,2200,2250,2300,2350,2400,2450,2500,2550,2600,2650,2700,2750,2800,2850,2900,2950,3000,3050,3100,3150,3200,3250,3300,3350,3400,3450,3500,3550,3600,3650,3700,3750,3800,3850,3900,3950,4000,4050,4100,4150,4200,4250,4300,4350,4400,4450,4500,4550,4600,4650,4700,4750,4800,4850,4900,4950,5000,5050,5100,5150,5200,5250,5300,5350],[0,90,100,110,120,130,140,150,160,170,180,190,200,210,220,230,240,250,260,270,280,290,300,310,320,330,340,350,360,370,380,390,400,410,420,430,440,450,460,470,480,490,500,510,520,530,540,550,560,570,580,590,600,610,620,630,640,650,660,670,680,690,700,710,720,730,740,750,760,770,780,790,800,810,820,830,840,850,860,870,880,890,900,910,920,930,940,950,960,970,980,990,1000,1010,1020,1030,1040,1050,1060,1070],[1,16,18,19,20,21,23,24,25,26,28,29,30,31,33,34,35,36,38,39,40,41,43,44,45,46,48,49,50,51,53,54,55,56,58,59,60,61,63,64,65,66,68,69,70,71,73,74,75,76,77,79,80,81,82,84,85,86,87,89,90,91,92,94,95,96,97,99,100,101,102,104,105,106,107,109,110,111,112,114,115,116,117,119,120,121,122,124,125,126,127,129,130,131,132,134,135,136,137,138],[0,16,18,19,20,21,23,24,25,26,28,29,30,31,33,34,35,36,38,39,40,41,43,44,45,46,48,49,50,51,53,54,55,56,58,59,60,61,63,64,65,66,68,69,70,71,73,74,75,76,77,79,80,81,82,84,85,86,87,89,90,91,92,94,95,96,97,99,100,101,102,104,105,106,107,109,110,111,112,114,115,116,117,119,120,121,122,124,125,126,127,129,130,131,132,134,135,136,137,138],[0,16,18,19,20,21,23,24,25,26,28,29,30,31,33,34,35,36,38,39,40,41,43,44,45,46,48,49,50,51,53,54,55,56,58,59,60,61,63,64,65,66,68,69,70,71,73,74,75,76,77,79,80,81,82,84,85,86,87,89,90,91,92,94,95,96,97,99,100,101,102,104,105,106,107,109,110,111,112,114,115,116,117,119,120,121,122,124,125,126,127,129,130,131,132,134,135,136,137,138],[0,16,18,19,20,21,23,24,25,26,28,29,30,31,33,34,35,36,38,39,40,41,43,44,45,46,48,49,50,51,53,54,55,56,58,59,60,61,63,64,65,66,68,69,70,71,73,74,75,76,77,79,80,81,82,84,85,86,87,89,90,91,92,94,95,96,97,99,100,101,102,104,105,106,107,109,110,111,112,114,115,116,117,119,120,121,122,124,125,126,127,129,130,131,132,134,135,136,137,138],[0,32,35,37,40,42,45,47,50,52,55,57,60,62,65,67,70,72,75,77,80,82,85,87,90,92,95,97,100,102,105,107,110,112,115,117,120,122,125,127,130,132,135,137,140,142,145,147,150,152,155,157,160,162,165,167,170,172,175,177,180,182,185,187,190,192,195,197,200,202,205,207,210,212,215,217,220,222,225,227,230,232,235,237,240,242,245,247,250,252,255,257,260,262,265,267,270,272,275,277],[0,32,35,37,40,42,45,47,50,52,55,57,60,62,65,67,70,72,75,77,80,82,85,87,90,92,95,97,100,102,105,107,110,112,115,117,120,122,125,127,130,132,135,137,140,142,145,147,150,152,155,157,160,162,165,167,170,172,175,177,180,182,185,187,190,192,195,197,200,202,205,207,210,212,215,217,220,222,225,227,230,232,235,237,240,242,245,247,250,252,255,257,260,262,265,267,270,272,275,277]]}""",
        ["commonevents.json"] =
            """{"id":0,"list":[{"code":0,"indent":0,"parameters":[]}],"name":"","switchId":1,"trigger":0}""",
        ["enemies.json"] =
            """{"id":0,"actions":[{"conditionParam1":0,"conditionParam2":0,"conditionType":0,"rating":5,"skillId":1}],"battlerHue":0,"battlerName":"","dropItems":[{"dataId":1,"denominator":1,"kind":0},{"dataId":1,"denominator":1,"kind":0},{"dataId":1,"denominator":1,"kind":0}],"exp":0,"traits":[{"code":22,"dataId":0,"value":0.95},{"code":22,"dataId":1,"value":0.05},{"code":31,"dataId":1,"value":0}],"gold":0,"name":"","note":"","params":[100,0,10,10,10,10,10,10]}""",
        ["items.json"] =
            """{"id":0,"animationId":0,"consumable":true,"damage":{"critical":false,"elementId":0,"formula":"0","type":0,"variance":20},"description":"","effects":[],"hitType":0,"iconIndex":0,"itypeId":1,"name":"","note":"","occasion":0,"price":0,"repeats":1,"scope":7,"speed":0,"successRate":100,"tpGain":0}""",
        ["skills.json"] =
            """{"id":0,"animationId":0,"damage":{"critical":false,"elementId":0,"formula":"0","type":0,"variance":20},"description":"","effects":[],"hitType":0,"iconIndex":0,"message1":"","message2":"","mpCost":0,"name":"","note":"","occasion":0,"repeats":1,"requiredWtypeId1":0,"requiredWtypeId2":0,"scope":1,"speed":0,"stypeId":1,"successRate":100,"tpCost":0,"tpGain":0,"messageType":1}""",
        ["states.json"] =
            """{"id":0,"autoRemovalTiming":0,"chanceByDamage":100,"iconIndex":0,"maxTurns":1,"message1":"","message2":"","message3":"","message4":"","minTurns":1,"motion":0,"name":"","note":"","overlay":0,"priority":50,"removeAtBattleEnd":false,"removeByDamage":false,"removeByRestriction":false,"removeByWalking":false,"restriction":0,"stepsToRemove":100,"traits":[],"messageType":1}""",
        ["tilesets.json"] =
            """{"id":0,"flags":[16],"mode":1,"name":"","note":"","tilesetNames":["","","","","","","","",""]}""",
        ["troops.json"] =
            """{"id":0,"members":[],"name":"","pages":[{"conditions":{"actorHp":50,"actorId":1,"actorValid":false,"enemyHp":50,"enemyIndex":0,"enemyValid":false,"switchId":1,"switchValid":false,"turnA":0,"turnB":0,"turnEnding":false,"turnValid":false},"list":[{"code":0,"indent":0,"parameters":[]}],"span":0}]}""",
        ["weapons.json"] =
            """{"id":0,"animationId":0,"description":"","etypeId":1,"traits":[{"code":31,"dataId":1,"value":0},{"code":22,"dataId":0,"value":0}],"iconIndex":0,"name":"","note":"","params":[0,0,0,0,0,0,0,0],"price":0,"wtypeId":0}"""
    };

    public static TheoryData<string> DatabaseFiles => new(MzEditorRecords.Keys);

    [Theory]
    [MemberData(nameof(DatabaseFiles))]
    public void BlankRecord_OnMz_IsTheEditorsRecordInItsKeyOrder(string file)
    {
        JObject blank = RpgDataSchema.BlankRecord("data/" + file, RpgMakerEngine.MZ)!;
        var editor = JObject.Parse(MzEditorRecords[file]);

        Assert.True(JToken.DeepEquals(editor, blank), file);
        Assert.Equal(editor.Properties().Select(property => property.Name), blank.Properties().Select(property => property.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RpgMakerEngine.MV)]
    public void BlankRecord_OnMvOrAnUnknownEngine_DropsWhatMvLacksAndUsesMvAnimations(RpgMakerEngine? engine)
    {
        foreach (string file in MzEditorRecords.Keys.Where(name => name != "animations.json"))
        {
            var expected = JObject.Parse(MzEditorRecords[file]);
            expected.Remove("messageType");
            Assert.True(JToken.DeepEquals(expected, RpgDataSchema.BlankRecord("data/" + file, engine)), file);
        }

        JObject animation = RpgDataSchema.BlankRecord("data/animations.json", engine)!;
        Assert.Equal(
            "{\"id\":0,\"animation1Hue\":0,\"animation1Name\":\"\",\"animation2Hue\":0,\"animation2Name\":\"\",\"frames\":[[]],\"name\":\"\",\"position\":1,\"timings\":[]}",
            animation.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Theory]
    [InlineData("data/mapinfos.json")]
    [InlineData("data/map001.json")]
    [InlineData("data/system.json")]
    [InlineData("data/quests.json")]
    public void BlankRecord_ForAFileThatKeepsItsHoles_IsNull(string file)
    {
        Assert.Null(RpgDataSchema.BlankRecord(file, RpgMakerEngine.MZ));
    }

    [Fact]
    public void BlankRecord_EachCall_IsANewObject()
    {
        JObject first = RpgDataSchema.BlankRecord("data/items.json", RpgMakerEngine.MZ)!;
        first["name"] = "Changed";

        Assert.Equal("", RpgDataSchema.BlankRecord("data/items.json", RpgMakerEngine.MZ)!["name"]!.Value<string>());
    }

    public static TheoryData<string, string, bool> BlankShapeCases => new()
    {
        { "data/items.json", "{\"id\":3,\"name\":\"\"}", true },
        { "data/items.json", "{\"id\":3,\"name\":\"\",\"note\":\"\",\"extra\":0,\"more\":[]}", true },
        { "data/items.json", "{\"id\":3,\"name\":\"\",\"price\":5}", false },
        { "data/items.json", "{\"id\":3,\"name\":\"\",\"extra\":\"x\"}", false },
        { "data/items.json", "{\"id\":3,\"name\":\"Herb\"}", false },
        { "data/items.json", "{\"id\":3,\"price\":5}", false },
        { "data/actors.json", "{\"id\":3,\"name\":\"\",\"battlerName\":\"Hero\"}", false },
        { "data/actors.json", "{\"id\":3,\"name\":\"\",\"equips\":[]}", true },
        { "data/commonevents.json", "{\"id\":3,\"name\":\"\",\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}]}", true },
        { "data/commonevents.json", "{\"id\":3,\"name\":\"\",\"list\":[{\"code\":117,\"indent\":0,\"parameters\":[1]},{\"code\":0,\"indent\":0,\"parameters\":[]}]}", false },
        { "data/commonevents.json", "{\"id\":3,\"name\":\"\",\"list\":[{\"code\":117,\"indent\":0,\"parameters\":[1]}]}", false },
        { "data/commonevents.json", "{\"id\":3,\"name\":\"\",\"list\":[]}", true },
        { "data/troops.json", "{\"id\":3,\"name\":\"\",\"pages\":[{\"list\":[{\"code\":0,\"indent\":0,\"parameters\":[]}],\"span\":0}]}", true },
        { "data/troops.json", "{\"id\":3,\"name\":\"\",\"pages\":[{\"list\":[{\"code\":0}]},{\"list\":[{\"code\":101},{\"code\":0}]}]}", false },

        // Maps, map events and the map tree are never filler.
        { "data/mapinfos.json", "{\"id\":3,\"name\":\"\"}", false },
        { "data/map001.json", "{\"id\":3,\"name\":\"\",\"pages\":[]}", false }
    };

    [Theory]
    [MemberData(nameof(BlankShapeCases))]
    public void HasBlankShape_FollowsTheRuleText(string file, string record, bool blank)
    {
        Assert.Equal(blank, RpgDataSchema.HasBlankShape(file, JToken.Parse(record)));
    }

    [Theory]
    [MemberData(nameof(DatabaseFiles))]
    public void HasBlankShape_EveryEditorBlank_IsTrue(string file)
    {
        Assert.True(RpgDataSchema.HasBlankShape("data/" + file, JObject.Parse(MzEditorRecords[file])));
    }

    [Theory]
    [MemberData(nameof(DatabaseFiles))]
    public void HasBlankShape_EveryMvBlank_IsTrue(string file)
    {
        JObject blank = RpgDataSchema.BlankRecord("data/" + file, RpgMakerEngine.MV)!;
        blank["id"] = 3;

        Assert.True(RpgDataSchema.HasBlankShape("data/" + file, blank));
    }

    private static string ItemsAdding(params JToken[] records)
    {
        return EditItems(items =>
        {
            foreach (JToken record in records)
            {
                items.Add(record);
            }
        });
    }

    private static JObject Filler(int id, int price = 0)
    {
        JObject record = RpgDataSchema.BlankRecord("data/items.json", RpgMakerEngine.MZ)!;
        record["id"] = id;
        record["price"] = price;
        return record;
    }

    [Fact]
    public void Compose_FillerAgainstARealRecord_YieldsWithoutAClash()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource filler = FileMod("a-tin", Items, ItemsAdding(Filler(3)), 1);
        ModSource real = FileMod("b-oak", Items, ItemsAdding(new JObject { ["id"] = 3, ["name"] = "Herb" }), 0);

        MergePlan plan = Compose(bases, filler, real);

        Assert.Empty(plan.Collisions);
        Assert.True(plan.IsDeployable);
        MergedOp kept = Assert.Single(plan.Ops);
        Assert.Equal(["b-oak"], kept.ModIds);
        Assert.Equal("Herb", kept.Op.Value!["name"]!.Value<string>());
    }

    [Fact]
    public void Compose_FillerFromTwoMods_IsNoClashAndKeepsTheLatestInProfileOrder()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource early = FileMod("a-tin", Items, ItemsAdding(Filler(3)), 0);
        ModSource late = FileMod("b-oak", Items, ItemsAdding(new JObject { ["id"] = 3, ["name"] = "" }), 1);

        MergePlan plan = Compose(bases, early, late);

        Assert.Empty(plan.Collisions);
        Assert.Equal(["b-oak"], Assert.Single(plan.Ops).ModIds);
    }

    [Fact]
    public void Compose_BlankShapedRecordWithData_IsRealAndClashes()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource priced = FileMod("a-tin", Items, ItemsAdding(Filler(3, price: 5)), 0);
        ModSource herb = FileMod("b-oak", Items, ItemsAdding(new JObject { ["id"] = 3, ["name"] = "Herb" }), 1);

        Collision clash = Assert.Single(Compose(bases, priced, herb).Collisions);

        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.Equal(["a-tin", "b-oak"], clash.ModIds);
    }

    [Fact]
    public void Compose_TwoRealRecordsBesideFiller_StillClash()
    {
        BaseFileSet bases = Bases(Base(Items, VanillaItems));
        ModSource filler = FileMod("a-tin", Items, ItemsAdding(Filler(3)), 0);
        ModSource herb = FileMod("b-oak", Items, ItemsAdding(new JObject { ["id"] = 3, ["name"] = "Herb" }), 1);
        ModSource salve = FileMod("c-elm", Items, ItemsAdding(new JObject { ["id"] = 3, ["name"] = "Salve" }), 2);

        Collision clash = Assert.Single(Compose(bases, filler, herb, salve).Collisions);

        Assert.Equal(CollisionKind.IdClash, clash.Kind);
        Assert.Equal(["b-oak", "c-elm"], clash.ModIds);
    }

    [Theory]
    [InlineData(RpgMakerEngine.MZ, true)]
    [InlineData(RpgMakerEngine.MV, false)]
    [InlineData(null, false)]
    public void Write_HoleBeforeAnAddedRecord_TakesTheBlankRecordOfTheEngine(RpgMakerEngine? engine, bool messageType)
    {
        const string skills = "data/Skills.json";
        BaseFileSet bases = Bases(Base(skills, "[null,{\"id\":1,\"name\":\"Strike\"}]"));
        ModSource mod = FileMod("a-tin", skills, "[null,{\"id\":1,\"name\":\"Strike\"},null,{\"id\":3,\"name\":\"Flare\"}]");

        MergePlan plan = Compose(bases, mod);
        var written = JArray.Parse(Assert.Single(MergedFileWriter.Write(plan, bases, null, engine)).Text!);

        Assert.Equal(JTokenType.Null, written[0].Type);
        var hole = (JObject)written[2];
        Assert.Equal(2, hole["id"]!.Value<int>());
        Assert.Equal("", hole["name"]!.Value<string>());
        Assert.Equal(messageType, hole.ContainsKey("messageType"));
        Assert.Equal("Flare", written[3]["name"]!.Value<string>());
    }

    [Fact]
    public void Write_MapTreeAndMapEvents_KeepTheirHoles()
    {
        const string infos = "data/MapInfos.json";
        const string infosText = "[null,{\"id\":1,\"name\":\"A\"},null,{\"id\":3,\"name\":\"C\"}]";
        string mapText = EditMap(map => map["events"]![1] = JValue.CreateNull());
        BaseFileSet bases = Bases(
            Base(infos, "[null,{\"id\":1,\"name\":\"A\"},{\"id\":2,\"name\":\"B\"},{\"id\":3,\"name\":\"C\"}]"),
            Base(Map, VanillaMap));
        ModSource mod = new(
            "a-tin",
            0,
            [
                new ModFile(infos, "files/MapInfos.json", infosText, Hash(infosText)),
                new ModFile(Map, "files/Map001.json", mapText, Hash(mapText))
            ],
            [],
            []);

        IReadOnlyList<MergedFile> files = MergedFileWriter.Write(Compose(bases, mod), bases, null, RpgMakerEngine.MZ);

        Assert.Equal(JTokenType.Null, JArray.Parse(files.Single(file => file.File == "data/mapinfos.json").Text!)[2].Type);
        Assert.Equal(JTokenType.Null, JObject.Parse(files.Single(file => file.File == "data/map001.json").Text!)["events"]![1]!.Type);
    }
}
