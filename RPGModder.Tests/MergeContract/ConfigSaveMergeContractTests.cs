using LZStringCSharp;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class ConfigSaveMergeContractTests
{
    [Fact]
    public void Write_ConfigRpgsaveRoundTrip_UpdatesChangedTopLevelKeyAndPreservesMissingKeys()
    {
        const string liveConfigJson = "{\"alwaysDash\":false,\"commandRemember\":true,\"bgmVolume\":100,\"bgsVolume\":80}";
        const string modConfigJson = "{\"bgmVolume\":60}";

        string liveEncoded = LZString.CompressToBase64(liveConfigJson);
        string modEncoded = LZString.CompressToBase64(modConfigJson);

        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile("save/config.rpgsave", liveEncoded)
        });

        ModSource mod = new(
            "a-config",
            0,
            new[] { CreateModFile("save/config.rpgsave", "save/config.rpgsave", modEncoded) },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        Assert.Empty(changeSet.Diagnostics);
        ChangeOp op = Assert.Single(changeSet.Ops);
        Assert.Equal("save/config.rpgsave", op.Key.File);
        Assert.Equal(ChangeKind.SetLeaf, op.Kind);
        Assert.Equal(60, op.Value!.Value<int>());

        MergePlan plan = Composer.Compose(new[] { changeSet }, Array.Empty<Resolution>(), baseFiles);
        Assert.True(plan.IsDeployable);

        IReadOnlyList<MergedFile> written = MergedFileWriter.Write(plan, baseFiles, systemVersionId: null);
        MergedFile mergedConfig = Assert.Single(written);
        Assert.Equal("save/config.rpgsave", mergedConfig.File);
        Assert.Equal(MergedFileSource.Text, mergedConfig.Source);
        Assert.NotNull(mergedConfig.Text);

        string decompressed = LZString.DecompressFromBase64(mergedConfig.Text!);
        JObject actualJson = JObject.Parse(decompressed);
        JObject expectedJson = JObject.Parse("{\"alwaysDash\":false,\"commandRemember\":true,\"bgmVolume\":60,\"bgsVolume\":80}");
        Assert.True(JToken.DeepEquals(expectedJson, actualJson));
    }

    [Theory]
    [InlineData("save/config.save")]
    public void Build_UnsupportedConfigSaveFormat_IsConfigSaveUnsupportedWithNoOp(string targetPath)
    {
        BaseFileSet baseFiles = BaseFileSet.From(new[]
        {
            CreateBaseFile(targetPath, "{\"bgmVolume\":100}")
        });

        ModSource mod = new(
            "a-mz-config",
            0,
            new[] { CreateModFile(targetPath, targetPath, "{\"bgmVolume\":50}") },
            Array.Empty<ModPatch>(),
            Array.Empty<PluginEntry>());

        ModChangeSet changeSet = ChangeSetBuilder.Build(mod, baseFiles);

        OperationDiagnostic diag = Assert.Single(changeSet.Diagnostics);
        Assert.Equal(DiagnosticCodes.MergeConfigSaveUnsupported, diag.Code);
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        Assert.Empty(changeSet.Ops);
    }

    private static BaseFile CreateBaseFile(string path, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new BaseFile(path, text, sha);
    }

    private static ModFile CreateModFile(string target, string source, string text)
    {
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ModFile(target, source, text, sha);
    }
}
