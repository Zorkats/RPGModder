using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Tests;

public sealed class ConflictDetectionServiceTests
{
    [Fact]
    public void GenerateReport_ExcludesDisabledMods()
    {
        ModListItem enabled = CreateFileMod("Enabled", true, "img/picture.png");
        ModListItem disabled = CreateFileMod("Disabled", false, "img/picture.png");

        ConflictReport report = new ConflictDetectionService().GenerateReport(new[] { enabled, disabled });

        Assert.False(report.HasConflicts);
    }

    [Fact]
    public void GenerateReport_IdentifiesLastFileReplacementAsWinner()
    {
        ModListItem first = CreateFileMod("First", true, "img/picture.png");
        ModListItem second = CreateFileMod("Second", true, "img/picture.png");

        FileConflict conflict = Assert.Single(new ConflictDetectionService()
            .GenerateReport(new[] { first, second }).Conflicts);

        Assert.Equal(ConflictKind.FileOverwrite, conflict.Kind);
        Assert.Equal("Second", conflict.Winner);
        Assert.False(conflict.IsMergeable);
    }

    [Fact]
    public void GenerateReport_ClassifiesJsonPatchesAsSemanticMerge()
    {
        ModListItem first = CreatePatchMod("First", "data/System.json");
        ModListItem second = CreatePatchMod("Second", "data/System.json");

        FileConflict conflict = Assert.Single(new ConflictDetectionService()
            .GenerateReport(new[] { first, second }).Conflicts);

        Assert.Equal(ConflictKind.SemanticMerge, conflict.Kind);
        Assert.Equal("Merged", conflict.Winner);
        Assert.True(conflict.IsMergeable);
    }

    private static ModListItem CreateFileMod(string name, bool enabled, string target)
    {
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Name = name },
            FileOps = new List<FileOperation> { new() { Source = "payload", Target = target } }
        };
        return new ModListItem(manifest, name, enabled);
    }

    private static ModListItem CreatePatchMod(string name, string target)
    {
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Name = name },
            JsonPatches = new List<JsonPatch> { new() { Target = target, MergeData = new JObject() } }
        };
        return new ModListItem(manifest, name, true);
    }
}
