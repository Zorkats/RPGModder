using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class ModChangeSetSerializerContractTests
{
    [Fact]
    public void Serializer_RoundTrip_KeepsOpsAndDiagnosticsAndTakesTheNewPriority()
    {
        ModChangeSet original = new(
            "a-roundtrip",
            1,
            new[]
            {
                new ChangeOp(
                    "a-roundtrip",
                    new RecordKey("data/items.json", "id:1"),
                    "price",
                    ChangeKind.SetLeaf,
                    new JValue(75),
                    "data/Items.json"),
                new ChangeOp(
                    "a-roundtrip",
                    new RecordKey("data/items.json", "id:2"),
                    "",
                    ChangeKind.RemoveRecord,
                    null,
                    "data/Items.json")
            },
            new[]
            {
                new OperationDiagnostic(
                    DiagnosticCodes.MergeParseFailed,
                    "Parse failed for broken file.",
                    DiagnosticSeverity.Error,
                    "a-roundtrip")
            });

        string text = ModChangeSetSerializer.Serialize(original);
        ModChangeSet? restored = ModChangeSetSerializer.Deserialize(text, priority: 9);

        Assert.NotNull(restored);
        Assert.Equal("a-roundtrip", restored!.ModId);
        Assert.Equal(9, restored.Priority);
        Assert.Equal(original.Ops.Count, restored.Ops.Count);

        for (int i = 0; i < original.Ops.Count; i++)
        {
            ChangeOp expectedOp = original.Ops[i];
            ChangeOp actualOp = restored.Ops[i];
            Assert.Equal(expectedOp.ModId, actualOp.ModId);
            Assert.Equal(expectedOp.Key, actualOp.Key);
            Assert.Equal(expectedOp.Path, actualOp.Path);
            Assert.Equal(expectedOp.Kind, actualOp.Kind);
            Assert.Equal(expectedOp.SourceFile, actualOp.SourceFile);
            Assert.True(JToken.DeepEquals(expectedOp.Value, actualOp.Value));
        }

        OperationDiagnostic restoredDiag = Assert.Single(restored.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, restoredDiag.Code);
        Assert.Equal("Parse failed for broken file.", restoredDiag.Message);
        Assert.Equal(DiagnosticSeverity.Error, restoredDiag.Severity);
        Assert.Equal("a-roundtrip", restoredDiag.Subject);
    }

    [Fact]
    public void Serializer_RoundTrip_PreservesReplaceFileEventAndPluginOpsAndAfterPath()
    {
        ModChangeSet original = new(
            "a-b2-roundtrip",
            0,
            new[]
            {
                new ChangeOp(
                    "a-b2-roundtrip",
                    new RecordKey("img/pictures/banner.png", "*"),
                    "",
                    ChangeKind.ReplaceFile,
                    new JValue("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
                    "img/pictures/banner.png"),
                new ChangeOp(
                    "a-b2-roundtrip",
                    new RecordKey("data/map001.json", "event:1"),
                    "pages[0].trigger",
                    ChangeKind.SetLeaf,
                    new JValue(2),
                    "data/Map001.json"),
                new ChangeOp(
                    "a-b2-roundtrip",
                    new RecordKey("js/plugins.js", "plugin:AshExtra"),
                    "",
                    ChangeKind.AddRecord,
                    JObject.Parse("{\"name\":\"AshExtra\",\"status\":true,\"description\":\"Extra\",\"parameters\":{\"mode\":\"fast\"}}"),
                    "js/plugins.js"),
                new ChangeOp(
                    "a-b2-roundtrip",
                    new RecordKey("js/plugins.js", "plugin:AshExtra"),
                    "@after",
                    ChangeKind.SetLeaf,
                    new JValue("Omega"),
                    "js/plugins.js")
            },
            Array.Empty<OperationDiagnostic>());

        string text = ModChangeSetSerializer.Serialize(original);
        ModChangeSet? restored = ModChangeSetSerializer.Deserialize(text, priority: 4);

        Assert.NotNull(restored);
        Assert.Equal("a-b2-roundtrip", restored!.ModId);
        Assert.Equal(4, restored.Priority);
        Assert.Equal(original.Ops.Count, restored.Ops.Count);

        for (int i = 0; i < original.Ops.Count; i++)
        {
            ChangeOp expectedOp = original.Ops[i];
            ChangeOp actualOp = restored.Ops[i];
            Assert.Equal(expectedOp.ModId, actualOp.ModId);
            Assert.Equal(expectedOp.Key, actualOp.Key);
            Assert.Equal(expectedOp.Path, actualOp.Path);
            Assert.Equal(expectedOp.Kind, actualOp.Kind);
            Assert.Equal(expectedOp.SourceFile, actualOp.SourceFile);
            Assert.True(JToken.DeepEquals(expectedOp.Value, actualOp.Value));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not valid json")]
    [InlineData("{unclosed")]
    [InlineData("[]")]
    public void Deserialize_MalformedText_ReturnsNull(string garbage)
    {
        ModChangeSet? result = ModChangeSetSerializer.Deserialize(garbage, priority: 0);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_OtherSchemaVersion_ReturnsNull()
    {
        ModChangeSet original = new(
            "a-ver",
            0,
            Array.Empty<ChangeOp>(),
            Array.Empty<OperationDiagnostic>());

        string text = ModChangeSetSerializer.Serialize(original);
        JObject root = JObject.Parse(text);

        foreach (JProperty prop in root.Properties().ToList())
        {
            if (prop.Value.Type == JTokenType.Integer && prop.Value.Value<int>() == RpgDataSchema.SchemaVersion)
            {
                prop.Value = RpgDataSchema.SchemaVersion + 100;
            }
        }

        ModChangeSet? result = ModChangeSetSerializer.Deserialize(root.ToString(), priority: 0);
        Assert.Null(result);
    }
}
