using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

public sealed class MergeSurfaceTests
{
    [Theory]
    [InlineData("data\\Items.json", "data/items.json")]
    [InlineData("./data/Items.json", "data/items.json")]
    [InlineData("/js/plugins.js", "js/plugins.js")]
    [InlineData("img/Faces/Hero.png", "img/faces/hero.png")]
    public void ToKey_NormalizesSlashesPrefixAndCase(string path, string key)
    {
        Assert.Equal(key, MergePath.ToKey(path));
    }

    [Theory]
    [InlineData("data/Items.json", MergeFileKind.Data)]
    [InlineData("data\\Map001.json", MergeFileKind.Data)]
    [InlineData("data/sub/Extra.json", MergeFileKind.Opaque)]
    [InlineData("js/plugins.js", MergeFileKind.PluginList)]
    [InlineData("js/plugins/Tool.js", MergeFileKind.Opaque)]
    [InlineData("save/config.rpgsave", MergeFileKind.ConfigSave)]
    [InlineData("save/config.rmmzsave", MergeFileKind.ConfigSave)]
    [InlineData("save/file1.rpgsave", MergeFileKind.Opaque)]
    [InlineData("img/pictures/Logo.png", MergeFileKind.Opaque)]
    public void KindOf_FollowsTheFileTable(string path, MergeFileKind kind)
    {
        Assert.Equal(kind, RpgDataSchema.KindOf(path));
    }

    [Fact]
    public void BaseFileSet_FindsByKey_AndRejectsTwoEntriesForOneKey()
    {
        BaseFile items = Base("data/Items.json", VanillaItems);
        BaseFileSet set = Bases(items);

        Assert.Same(items, set.Find("DATA\\items.json"));
        Assert.Null(set.Find("data/Skills.json"));
        Assert.Throws<ArgumentException>(() => BaseFileSet.From([items, Base("data/items.json", "[]")]));
    }

    [Fact]
    public void Serializer_RoundTripsOpsAndDiagnostics_WithTheNewPriority()
    {
        var set = new ModChangeSet(
            "a-tin",
            4,
            [
                new ChangeOp("a-tin", new RecordKey("data/items.json", "id:1"), "note", ChangeKind.SetLeaf, JValue.CreateNull(), "Items.json"),
                new ChangeOp("a-tin", new RecordKey("data/items.json", "id:2"), "", ChangeKind.RemoveRecord, null, null),
                new ChangeOp("a-tin", new RecordKey("data/system.json", "*"), "['a.b'][2]", ChangeKind.SetLeaf, new JArray(1, 2), null)
            ],
            [new OperationDiagnostic(DiagnosticCodes.MergeParseFailed, "Broken.", DiagnosticSeverity.Error, "a-tin")]);

        string text = ModChangeSetSerializer.Serialize(set);
        ModChangeSet? read = ModChangeSetSerializer.Deserialize(text, 9);

        Assert.NotNull(read);
        Assert.Equal(["schema", "mod", "ops", "diagnostics"], JObject.Parse(text).Properties().Select(property => property.Name));
        Assert.Equal(9, read.Priority);
        Assert.Equal("a-tin", read.ModId);
        Assert.Equal(set.Ops.Select(Describe), read.Ops.Select(Describe));
        Assert.Equal(set.Ops.Select(op => op.SourceFile), read.Ops.Select(op => op.SourceFile));
        Assert.Equal(JTokenType.Null, read.Ops[0].Value!.Type);
        Assert.Null(read.Ops[1].Value);
        Assert.Equal(set.Diagnostics, read.Diagnostics);
        Assert.Equal(text, ModChangeSetSerializer.Serialize(read));
    }

    [Fact]
    public void Serializer_RoundTripsTheFileFamilyShapes()
    {
        var set = new ModChangeSet(
            "a-tin",
            0,
            [
                new ChangeOp("a-tin", new RecordKey("data/map001.json", "event:3"), "pages[0].list", ChangeKind.EditCommands, new JArray(), "Map001.json"),
                new ChangeOp("a-tin", new RecordKey("js/plugins.js", "plugin:Font Tool"), "", ChangeKind.AddRecord, new JObject { ["name"] = "Font Tool" }, null),
                new ChangeOp("a-tin", new RecordKey("js/plugins.js", "plugin:Font Tool"), "@after", ChangeKind.SetLeaf, JValue.CreateNull(), null),
                new ChangeOp("a-tin", new RecordKey("js/plugins.js", "plugin:Omega"), "@after", ChangeKind.SetLeaf, new JValue("Alpha"), null),
                new ChangeOp("a-tin", new RecordKey("img/a.png", "*"), "", ChangeKind.ReplaceFile, new JValue("ab"), "a.png"),
                new ChangeOp("a-tin", new RecordKey("data/extra.json", "*"), "", ChangeKind.AddRecord, new JArray(), "Extra.json")
            ],
            []);

        string text = ModChangeSetSerializer.Serialize(set);
        ModChangeSet? read = ModChangeSetSerializer.Deserialize(text, 0);

        Assert.NotNull(read);
        Assert.Equal(set.Ops.Select(Describe), read.Ops.Select(Describe));
        Assert.Equal(set.Ops.Select(op => op.SourceFile), read.Ops.Select(op => op.SourceFile));
        Assert.Equal(text, ModChangeSetSerializer.Serialize(read));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schema\":2,\"mod\":\"a\",\"ops\":[],\"diagnostics\":[]}")]
    [InlineData("{\"schema\":1,\"mod\":\"a\",\"ops\":[{\"file\":\"f\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"Explode\"}],\"diagnostics\":[]}")]
    [InlineData("{\"schema\":1,\"mod\":\"a\",\"ops\":[{\"file\":\"f\",\"record\":\"*\",\"path\":\"x[\",\"kind\":\"SetLeaf\"}],\"diagnostics\":[]}")]
    [InlineData("{\"schema\":1,\"mod\":\"a\",\"ops\":[],\"diagnostics\":[{\"code\":\"c\",\"message\":\"m\",\"severity\":\"7\"}]}")]
    public void Deserialize_ReturnsNullForTextItCannotTrust(string text)
    {
        Assert.Null(ModChangeSetSerializer.Deserialize(text, 0));
    }

    [Theory]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"SetLeaf,RemoveLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"x\",\"kind\":\" SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"SetLeaf\"}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"\",\"kind\":\"RemoveLeaf\"}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"RemoveLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"banana\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"id:01\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"*\",\"path\":\"\",\"kind\":\"RemoveRecord\"}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"id:3\",\"path\":\"x\",\"kind\":\"AddRecord\",\"value\":{}}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"id:3\",\"path\":\"effects\",\"kind\":\"EditSequence\",\"value\":1}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"a[01]\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/system.json\",\"record\":\"*\",\"path\":\"['a']\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/System.json\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"id:1\",\"path\":\"x\",\"kind\":\"ReplaceFile\",\"value\":1}")]
    [InlineData("{\"file\":\"img/a.png\",\"record\":\"*\",\"path\":\"\",\"kind\":\"ReplaceFile\",\"value\":\"ab\"}")]
    [InlineData("{\"file\":\"img/a.png\",\"record\":\"*\",\"path\":\"\",\"kind\":\"ReplaceFile\",\"value\":1,\"source\":\"a.png\"}")]
    [InlineData("{\"file\":\"img/a.png\",\"record\":\"id:1\",\"path\":\"\",\"kind\":\"ReplaceFile\",\"value\":\"ab\",\"source\":\"a.png\"}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"plugin:\",\"path\":\"status\",\"kind\":\"SetLeaf\",\"value\":true}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"id:1\",\"path\":\"@after\",\"kind\":\"SetLeaf\",\"value\":\"A\"}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"plugin:B\",\"path\":\"@after\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"plugin:B\",\"path\":\"@after\",\"kind\":\"RemoveLeaf\"}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"plugin:B\",\"path\":\"@before\",\"kind\":\"SetLeaf\",\"value\":\"A\"}")]
    [InlineData("{\"file\":\"data/map001.json\",\"record\":\"event:01\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"*\",\"path\":\"\",\"kind\":\"ReplaceFile\",\"value\":\"abc\",\"source\":\"Items.json\"}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"event:1\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"plugin:A\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"data/items.json\",\"record\":\"plugin:A\",\"path\":\"@after\",\"kind\":\"SetLeaf\",\"value\":null}")]
    [InlineData("{\"file\":\"data/map001.json\",\"record\":\"id:1\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"img/a.png\",\"record\":\"*\",\"path\":\"\",\"kind\":\"AddRecord\",\"value\":{}}")]
    [InlineData("{\"file\":\"img/a.png\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"id:1\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"plugin:A\",\"path\":\"list\",\"kind\":\"EditCommands\",\"value\":[]}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"js/plugins.js\",\"record\":\"*\",\"path\":\"\",\"kind\":\"SetLeaf\",\"value\":{}}")]
    [InlineData("{\"file\":\"save/config.rpgsave\",\"record\":\"*\",\"path\":\"x\",\"kind\":\"RemoveLeaf\"}")]
    [InlineData("{\"file\":\"save/config.rpgsave\",\"record\":\"*\",\"path\":\"a.b\",\"kind\":\"SetLeaf\",\"value\":1}")]
    [InlineData("{\"file\":\"save/config.rpgsave\",\"record\":\"*\",\"path\":\"\",\"kind\":\"SetLeaf\",\"value\":{}}")]
    public void Deserialize_ReturnsNullForAnOpTheBuilderCannotWrite(string op)
    {
        string text = "{\"schema\":1,\"mod\":\"a\",\"ops\":[" + op + "],\"diagnostics\":[]}";

        Assert.Null(ModChangeSetSerializer.Deserialize(text, 0));
    }

    [Fact]
    public void Deserialize_ReturnsNullForASchemaTooLargeForALong()
    {
        Assert.Null(ModChangeSetSerializer.Deserialize("{\"schema\":99999999999999999999,\"mod\":\"a\",\"ops\":[],\"diagnostics\":[]}", 0));
    }
}
