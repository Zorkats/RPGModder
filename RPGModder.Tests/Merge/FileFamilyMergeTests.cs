using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using static RPGModder.Tests.Merge.MergeTestKit;

namespace RPGModder.Tests.Merge;

/// <summary>New data files, opaque files and the config save (seam 11.1).</summary>
public sealed class FileFamilyMergeTests
{
    private const string Extra = "data/Extra.json";
    private const string Logo = "img/pictures/Logo.png";
    private const string Config = "save/config.rpgsave";
    private const string MzConfig = "save/config.rmmzsave";

    private static string EncodeConfig(string json, string key = Config)
    {
        return ConfigSaveText.Encode(key, JObject.Parse(json));
    }

    private static JObject DecodeWritten(BaseFileSet bases, MergePlan plan, string key = Config)
    {
        MergedFile file = Assert.Single(MergedFileWriter.Write(plan, bases, null));
        Assert.Equal(MergedFileSource.Text, file.Source);
        Assert.True(ConfigSaveText.TryDecode(key, file.Text, out JObject? config, out _));
        return config;
    }

    [Fact]
    public void Write_NewDataFile_CopiesTheModsFile()
    {
        BaseFileSet bases = Bases();
        ModSource mod = FileMod("a-tin", Extra, "[null,{\"id\":1}]");

        ChangeOp op = Assert.Single(Build(mod, bases).Ops);
        MergedFile file = Assert.Single(MergedFileWriter.Write(Compose(bases, mod), bases, null));

        Assert.Equal("data/extra.json|*||AddRecord|[null,{\"id\":1}]", Describe(op));
        Assert.Equal(MergedFileSource.ModFile, file.Source);
        Assert.Equal("a-tin", file.ModId);
        Assert.Equal("files/Extra.json", file.SourceFile);
    }

    [Fact]
    public void Compose_SameNewDataFileWithOtherWhiteSpace_IsOneOp()
    {
        BaseFileSet bases = Bases();
        ModSource first = FileMod("a-tin", Extra, "[null,{\"id\":1}]");
        ModSource second = FileMod("b-oak", Extra, "[\n  null,\n  { \"id\": 1 }\n]");

        MergePlan plan = Compose(bases, first, second);

        Assert.Empty(plan.Collisions);
        Assert.Equal(["a-tin", "b-oak"], Assert.Single(plan.Ops).ModIds);
    }

    [Fact]
    public void Compose_DifferentNewDataFiles_AreAnIdClashThatPickModSettles()
    {
        BaseFileSet bases = Bases();
        ModSource first = FileMod("a-tin", Extra, "[null,{\"id\":1}]");
        ModSource second = FileMod("b-oak", Extra, "[null,{\"id\":2}]");
        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "b-oak", collision.Fingerprint)], first, second);
        MergedFile file = Assert.Single(MergedFileWriter.Write(plan, bases, null));

        Assert.Equal(CollisionKind.IdClash, collision.Kind);
        Assert.True(collision.Blocking);
        Assert.Equal("b-oak", file.ModId);
        Assert.Equal(MergedFileSource.ModFile, file.Source);
    }

    [Fact]
    public void Write_NewDataFileWithAPatch_IsWrittenAsText()
    {
        BaseFileSet bases = Bases();
        var mod = new ModSource(
            "a-tin",
            0,
            [new ModFile(Extra, "files/Extra.json", "{\"a\":1}", Hash("{\"a\":1}"))],
            [new ModPatch(Extra, JObject.Parse("{\"b\":2}"))],
            []);

        ChangeOp op = Assert.Single(Build(mod, bases).Ops);
        MergedFile file = Assert.Single(MergedFileWriter.Write(Compose(bases, mod), bases, null));

        Assert.Null(op.SourceFile);
        Assert.Equal(MergedFileSource.Text, file.Source);
        Assert.Equal(MergeJson.Write(JObject.Parse("{\"a\":1,\"b\":2}")), file.Text);
    }

    [Fact]
    public void Build_PatchOnAFileTheGameLacks_IsATargetMissing()
    {
        ModChangeSet set = Build(PatchMod("a-tin", Extra, "{\"b\":2}"), Bases());

        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergePatchTargetMissing, Assert.Single(set.Diagnostics).Code);
    }

    [Fact]
    public void Build_OpaqueFileEqualToVanilla_IsNoChange()
    {
        Assert.Empty(Build(FileMod("a-tin", Logo, "pixels"), Bases(Base(Logo, "pixels"))).Ops);
    }

    [Fact]
    public void Write_OpaqueFileThatDiffers_ReplacesTheFileByHash()
    {
        BaseFileSet bases = Bases(Base(Logo, "pixels"));
        ModSource mod = FileMod("a-tin", Logo, "other pixels");

        ChangeOp op = Assert.Single(Build(mod, bases).Ops);
        MergedFile file = Assert.Single(MergedFileWriter.Write(Compose(bases, mod), bases, null));

        Assert.Equal("img/pictures/logo.png|*||ReplaceFile|\"" + Hash("other pixels") + "\"", Describe(op));
        Assert.Equal(MergedFileSource.ModFile, file.Source);
        Assert.Equal("files/Logo.png", file.SourceFile);
    }

    [Fact]
    public void Compose_TwoDifferentOpaqueFiles_AreAFileClashOnlyPickModSettles()
    {
        BaseFileSet bases = Bases(Base(Logo, "pixels"));
        ModSource first = FileMod("a-tin", Logo, "tin pixels");
        ModSource second = FileMod("b-oak", Logo, "oak pixels");
        Collision collision = Assert.Single(Compose(bases, first, second).Collisions);

        MergePlan plan = Compose(bases, [new Resolution(collision.Id, ResolutionKind.PickMod, "a-tin", collision.Fingerprint)], first, second);

        Assert.Equal(CollisionKind.FileClash, collision.Kind);
        Assert.True(collision.Blocking);
        Assert.Equal([ResolutionKind.PickMod], collision.Allowed);
        Assert.Equal("a-tin", Assert.Single(MergedFileWriter.Write(plan, bases, null)).ModId);
    }

    [Fact]
    public void Compose_TwoEqualOpaqueFiles_AreOneCopy()
    {
        BaseFileSet bases = Bases();
        MergePlan plan = Compose(bases, FileMod("a-tin", Logo, "pixels"), FileMod("b-oak", Logo, "pixels"));

        Assert.Empty(plan.Collisions);
        Assert.Equal(["a-tin", "b-oak"], Assert.Single(MergedFileWriter.Write(plan, bases, null)).ModIds);
    }

    [Fact]
    public void Build_PatchOnAnOpaqueFile_IsRejected()
    {
        ModChangeSet set = Build(PatchMod("a-tin", Logo, "{\"a\":1}"), Bases(Base(Logo, "pixels")));

        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergePatchShapeMismatch, Assert.Single(set.Diagnostics).Code);
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void ConfigSaveText_EncodeThenDecode_RoundTrips(string key)
    {
        var config = JObject.Parse("{\"alwaysDash\":true,\"bgmVolume\":80,\"name\":\"Caf\u00e9 \u65e5\u672c\"}");

        Assert.True(ConfigSaveText.TryDecode(key, ConfigSaveText.Encode(key, config), out JObject? read, out _));
        Assert.True(JToken.DeepEquals(config, read));
    }

    [Fact]
    public void ConfigSaveText_MzForm_IsAZlibByteString()
    {
        string text = EncodeConfig("{\"alwaysDash\":true}", MzConfig);

        // One character per byte, starting with the zlib header byte 0x78.
        Assert.All(text, current => Assert.True(current <= byte.MaxValue));
        Assert.Equal((char)0x78, text[0]);
    }

    [Theory]
    [InlineData(Config, "")]
    [InlineData(Config, "not base64 at all!")]
    [InlineData(Config, "AAAA")]
    [InlineData(MzConfig, "")]
    [InlineData(MzConfig, "x\u009c not deflate")]
    [InlineData(MzConfig, "\u65e5\u672c")]
    public void ConfigSaveText_TextThatIsNotAnEncodedObject_FailsToDecode(string key, string text)
    {
        Assert.False(ConfigSaveText.TryDecode(key, text, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(Config, MzConfig)]
    [InlineData(MzConfig, Config)]
    public void ConfigSaveText_TheOtherFormsText_FailsToDecode(string key, string other)
    {
        Assert.False(ConfigSaveText.TryDecode(key, EncodeConfig("{\"alwaysDash\":true}", other), out _, out _));
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void Write_ConfigKeyChanged_KeepsTheKeysTheModLeftOut(string key)
    {
        BaseFileSet bases = Bases(Base(key, EncodeConfig("{\"alwaysDash\":false,\"bgmVolume\":100}", key)));
        ModSource mod = FileMod("a-tin", key, EncodeConfig("{\"alwaysDash\":true}", key));

        ChangeOp op = Assert.Single(Build(mod, bases).Ops);
        JObject written = DecodeWritten(bases, Compose(bases, mod), key);

        Assert.Equal($"{key}|*|alwaysDash|SetLeaf|true", Describe(op));
        Assert.True(JToken.DeepEquals(JObject.Parse("{\"alwaysDash\":true,\"bgmVolume\":100}"), written));
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void Write_NoLiveConfig_StartsFromAnEmptyObject(string key)
    {
        BaseFileSet bases = Bases();

        JObject written = DecodeWritten(bases, Compose(bases, FileMod("a-tin", key, EncodeConfig("{\"alwaysDash\":true}", key))), key);

        Assert.True(JToken.DeepEquals(JObject.Parse("{\"alwaysDash\":true}"), written));
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void Build_ConfigPatch_IsALeafPerTopLevelKey(string key)
    {
        BaseFileSet bases = Bases(Base(key, EncodeConfig("{\"alwaysDash\":false,\"bgmVolume\":100}", key)));

        ChangeOp op = Assert.Single(Build(PatchMod("a-tin", key, "{\"bgmVolume\":50}"), bases).Ops);

        Assert.Equal($"{key}|*|bgmVolume|SetLeaf|50", Describe(op));
    }

    [Fact]
    public void Compose_ConfigKeySetTwoWays_IsAValueClash()
    {
        BaseFileSet bases = Bases(Base(Config, EncodeConfig("{\"bgmVolume\":100}")));
        ModSource first = PatchMod("a-tin", Config, "{\"bgmVolume\":50}");
        ModSource second = PatchMod("b-oak", Config, "{\"bgmVolume\":20}");

        Assert.Equal(CollisionKind.ValueClash, Assert.Single(Compose(bases, first, second).Collisions).Kind);
    }

    [Fact]
    public void Build_ConfigFormThisVersionCannotRead_IsUnsupported()
    {
        ModChangeSet set = Build(FileMod("a-tin", "save/config.save", "opaque"), Bases());

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Empty(set.Ops);
        Assert.Equal(DiagnosticCodes.MergeConfigSaveUnsupported, diagnostic.Code);
        Assert.Equal("a-tin", diagnostic.Subject);
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void Build_ModConfigThatDoesNotDecode_IsAParseFailureOnTheMod(string key)
    {
        ModChangeSet set = Build(FileMod("a-tin", key, "not base64 at all!"), Bases());

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeParseFailed, diagnostic.Code);
        Assert.Equal("a-tin", diagnostic.Subject);
    }

    [Theory]
    [InlineData(Config)]
    [InlineData(MzConfig)]
    public void Build_LiveConfigThatDoesNotDecode_IsABaseFaultOnTheFile(string key)
    {
        ModChangeSet set = Build(PatchMod("a-tin", key, "{\"bgmVolume\":50}"), Bases(Base(key, "not base64 at all!")));

        OperationDiagnostic diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeBaseParseFailed, diagnostic.Code);
        Assert.Equal(key, diagnostic.Subject);
    }
}
