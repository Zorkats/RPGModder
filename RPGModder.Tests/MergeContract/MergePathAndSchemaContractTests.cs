using RPGModder.Core.Merge;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class MergePathAndSchemaContractTests
{
    [Theory]
    [InlineData("data/Items.json", "data/items.json")]
    [InlineData(@"data\Items.json", "data/items.json")]
    [InlineData("./data/Items.json", "data/items.json")]
    [InlineData(@".\data\Items.json", "data/items.json")]
    [InlineData("/data/System.json", "data/system.json")]
    [InlineData(@"\js\plugins.js", "js/plugins.js")]
    [InlineData("JS/Plugins.JS", "js/plugins.js")]
    public void ToKey_AnyPathForm_GivesTheLowerCaseKey(string rawPath, string expectedKey)
    {
        string actual = MergePath.ToKey(rawPath);
        Assert.Equal(expectedKey, actual);
    }

    [Theory]
    [InlineData("js/plugins.js", MergeFileKind.PluginList)]
    [InlineData(@"JS\Plugins.js", MergeFileKind.PluginList)]
    [InlineData("./js/plugins.js", MergeFileKind.PluginList)]
    [InlineData("config.rpgsave", MergeFileKind.ConfigSave)]
    [InlineData("save/config.rpgsave", MergeFileKind.ConfigSave)]
    [InlineData("config.save", MergeFileKind.ConfigSave)]
    [InlineData("config.rmmzsave", MergeFileKind.ConfigSave)]
    [InlineData("data/Items.json", MergeFileKind.Data)]
    [InlineData(@"data\System.json", MergeFileKind.Data)]
    [InlineData("./data/Map001.json", MergeFileKind.Data)]
    [InlineData("data/sub/extra.json", MergeFileKind.Opaque)]
    [InlineData("data/notes.txt", MergeFileKind.Opaque)]
    [InlineData("js/plugins/CustomHud.js", MergeFileKind.Opaque)]
    [InlineData("img/pictures/banner.png", MergeFileKind.Opaque)]
    public void KindOf_Path_FollowsTheFileTable(string path, MergeFileKind expectedKind)
    {
        MergeFileKind actual = RpgDataSchema.KindOf(path);
        Assert.Equal(expectedKind, actual);
    }

    [Fact]
    public void BaseFileSet_Find_UsesTheNormalizedKey()
    {
        BaseFile itemFile = new("data/Items.json", "[null]", "00");
        BaseFileSet set = BaseFileSet.From(new[] { itemFile });

        Assert.Same(itemFile, set.Find("data/items.json"));
        Assert.Same(itemFile, set.Find(@"./DATA\Items.json"));
        Assert.Null(set.Find("data/weapons.json"));
    }
}
