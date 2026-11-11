using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

/// <summary>
/// The vanilla baseline build: the store-proven backup (TX-23, TX-24, TST-19), the game-update
/// rebuild (TX-20, TST-17) and the audit of the weaker path (TX-21). No test needs Steam.
/// </summary>
public sealed class VanillaBaselineBuilderTests
{
    private const string Title = "img/pictures/title.png";
    private const string TitleContent = "title picture";
    private const string System = "data/System.json";

    // ---- TX-23: every file proven, taken from the live folder or the old backup ----

    [Fact]
    public void StoreBuild_TakesProvenLiveFiles_AndRecordsTheStoreIds()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.Equal(VanillaBaseline.StoreManifest, manifest.BaselineSource);
        Assert.Equal(SyntheticSteamLibrary.AppId, manifest.SteamAppId);
        Assert.Equal(SyntheticSteamLibrary.BuildId, manifest.SteamBuildId);
        VanillaDepot depot = Assert.Single(manifest.SteamDepots!);
        Assert.Equal(SyntheticSteamLibrary.DepotId, depot.DepotId);
        Assert.Equal(SyntheticSteamLibrary.ManifestId, depot.ManifestId);
        Assert.Contains(manifest.Files, file => file.Path == Title);
        Assert.Contains(manifest.Files, file => file.Path == System);
        Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("save/", StringComparison.Ordinal));
        Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
        fixture.AssertNoBackupOrConsistent();
    }

    [Fact]
    public void StoreBuild_TakesAChangedModTargetFromTheOldBackup()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();
        Assert.True(fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None).Built);
        fixture.InstallMod("TitleMod", Title, "modded title");
        fixture.WriteLive(Title, "modded title");

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Equal(BaselineQuestion.None, outcome.Question);
        Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
        Assert.Equal("modded title", fixture.Game.ReadContent(Title));
        fixture.AssertNoBackupOrConsistent();
    }

    [Fact]
    public void StoreBuild_AsksSteamToFetch_WhenAFileIsProvenNowhere_AndKeepsNoBackup()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        fixture.InstallMod("TitleMod", Title, "modded title");
        fixture.WriteLive(Title, "modded title");
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.False(outcome.Built);
        Assert.Equal(BaselineQuestion.StoreFetch, outcome.Question);
        Assert.Equal(1, outcome.FetchCount);
        Assert.Equal(SyntheticSteamLibrary.AppId, outcome.AppId);
        OperationDiagnostic fetch = Assert.Single(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceStoreFetchNeeded);
        Assert.Equal(DiagnosticSeverity.Info, fetch.Severity);
        Assert.Equal("1", fetch.Subject);
        Assert.False(fixture.Vanilla.IsComplete);
    }

    [Fact]
    public void StoreBuild_NeverBacksUpAFileTheStoreDoesNotList()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        fixture.WriteLive("img/pictures/extra.png", "not the store's");
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(new BaselineAnswers(Base: BaseChoice.UseStoreFiles));

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Equal(1, outcome.Differences!.NotListed);
        Assert.DoesNotContain("img/pictures/extra.png", fixture.Vanilla.Files);
        Assert.DoesNotContain(fixture.Vanilla.ReadManifest()!.Files, file => file.Path == "img/pictures/extra.png");
        Assert.True(File.Exists(fixture.Game.ContentFile("img/pictures/extra.png")));
    }

    [Fact]
    public void StoreBuild_NeverBacksUpALegacyLibraryEntry_ButKeepsOtherFilesOfMods()
    {
        using var fixture = new BaselineFixture();
        fixture.WriteLive("mods/OldMod/mod.json", "{\"Metadata\":{\"Id\":\"OldMod\"}}");
        fixture.WriteLive("mods/OldMod/plugin.js", "// old library entry");
        fixture.WriteLive("mods/readme.txt", "shipped with the game");
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Contains("mods/readme.txt", fixture.Vanilla.Files);
        Assert.DoesNotContain(fixture.Vanilla.Files, file => file.StartsWith("mods/OldMod", StringComparison.Ordinal));
        Assert.True(File.Exists(fixture.Game.ContentFile("mods/OldMod/plugin.js")));
    }

    [Fact]
    public void StoreBuild_InterruptedAtAnyStep_LeavesNoTrustedPartialBackup_AndARerunCompletes()
    {
        int calls = CountCalls(Prepare, Rebuild);
        Assert.True(calls > 3, $"Only {calls} destructive calls were counted.");

        for (int failAt = 1; failAt <= calls; failAt++)
        {
            using var fixture = new BaselineFixture();
            Prepare(fixture);
            var faulty = new FaultInjectingFileOps(failAt);

            Assert.Throws<IOException>(() => Rebuild(fixture, faulty));

            fixture.AssertNoBackupOrConsistent();
            BaselineOutcome rerun = Rebuild(fixture, new FileSystemOps());
            Assert.True(rerun.Built, $"Rerun after a failure at {faulty.FailedCall}: {Describe(rerun)}");
            Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
            Assert.False(File.Exists(fixture.Game.ContentFile("img/pictures/added.png")));
            fixture.AssertNoBackupOrConsistent();
        }

        static void Prepare(BaselineFixture fixture)
        {
            fixture.PublishStoreManifest();
            var files = new FileSystemOps();
            Assert.True(fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None).Built);
            fixture.InstallMod("TitleMod", Title, "modded title");
            fixture.InstallMod("AddMod", "img/pictures/added.png", "added by a mod");
            fixture.WriteLive(Title, "modded title");
            fixture.WriteLive("img/pictures/added.png", "added by a mod");
        }

        static BaselineOutcome Rebuild(BaselineFixture fixture, IFileSystemOps files)
        {
            return fixture.Builder(files, fixture.Steam(new FileSystemOps())).Build(BaselineAnswers.None);
        }
    }

    // ---- TX-24: differences the store does not explain ask the player first ----

    [Fact]
    public void StoreBuild_AsksBeforeReplacingAnything_WhenTheLiveFolderDiffers()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();
        Assert.True(fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None).Built);
        string manifestBefore = File.ReadAllText(fixture.Vanilla.ManifestPath);
        fixture.WriteLive(Title, "the player's own title");
        File.Delete(fixture.Game.ContentFile("audio/bgm/theme.ogg"));

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.False(outcome.Built);
        Assert.Equal(BaselineQuestion.ChooseBase, outcome.Question);
        Assert.Equal(1, outcome.Differences!.Changed);
        Assert.Equal(1, outcome.Differences.Missing);
        Assert.Equal(0, outcome.Differences.NotListed);
        Assert.Equal(manifestBefore, File.ReadAllText(fixture.Vanilla.ManifestPath));
        Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
        Assert.Equal("the player's own title", fixture.Game.ReadContent(Title));
    }

    [Fact]
    public void StoreBuild_KeepMyFiles_RecordsAPlayerModifiedBaseWithTheCounts()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        fixture.WriteLive(Title, "the player's own title");
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(new BaselineAnswers(Base: BaseChoice.KeepMyFiles));

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.PlayerModified, manifest.Baseline);
        Assert.Equal(1, manifest.BaseDifferences!.Changed);
        Assert.False(manifest.BlocksDeploy);
        Assert.True(manifest.WarnsOnDeploy);
        Assert.Equal("the player's own title", fixture.Vanilla.ReadText(Title));
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBaselineUnverified && item.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StoreBuild_UseStoreFiles_TakesTheProvenCopyFromTheOldBackup()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();
        Assert.True(fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None).Built);
        fixture.WriteLive(Title, "the player's own title");

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(new BaselineAnswers(Base: BaseChoice.UseStoreFiles));

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
    }

    // ---- TX-20 and TST-17: the game-update rebuild ----

    [Fact]
    public void GameUpdate_WithTheStore_RecordsTheNewBuild_AndRemovesFilesRpgModderAdded()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        var files = new FileSystemOps();
        Assert.True(fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None).Built);
        fixture.InstallMod("AddMod", "img/pictures/added.png", "added by a mod");
        fixture.WriteLive("img/pictures/added.png", "added by a mod");
        fixture.WriteLive(System, "{\"gameTitle\":\"Updated\"}");
        File.Delete(fixture.Game.ContentFile("img/pictures/added.png"));
        fixture.PublishStoreManifest(buildId: "778");
        fixture.WriteLive("img/pictures/added.png", "added by a mod");

        BaselineOutcome outcome = fixture.Builder(files, fixture.Steam(files)).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal("778", manifest.SteamBuildId);
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.Equal("{\"gameTitle\":\"Updated\"}", fixture.Vanilla.ReadText(System));
        Assert.False(File.Exists(fixture.Game.ContentFile("img/pictures/added.png")));
        Assert.DoesNotContain("img/pictures/added.png", fixture.Vanilla.Files);
    }

    [Fact]
    public void GameUpdate_WithoutTheStore_RemovesAddedFiles_AndLeavesLegacyEntriesAlone()
    {
        using var fixture = new BaselineFixture();
        var files = new FileSystemOps();
        Assert.True(fixture.Builder(files, new NoStoreManifestSource()).Build(BaselineAnswers.None).Built);
        fixture.InstallMod("AddMod", "img/pictures/added.png", "added by a mod");
        fixture.WriteLive("img/pictures/added.png", "added by a mod");
        fixture.WriteLive("mods/OldMod/mod.json", "{\"Metadata\":{\"Id\":\"OldMod\"}}");
        fixture.WriteLive("mods/OldMod/plugin.js", "// old library entry");
        fixture.WriteLive(System, "{\"gameTitle\":\"Updated\"}");

        BaselineOutcome outcome = fixture.Builder(files, new NoStoreManifestSource()).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceStoreManifestUnavailable && item.Severity == DiagnosticSeverity.Info);
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.Equal(VanillaBaseline.Workflow, manifest.BaselineSource);
        Assert.Null(manifest.SteamBuildId);
        Assert.Equal("{\"gameTitle\":\"Updated\"}", fixture.Vanilla.ReadText(System));
        Assert.False(File.Exists(fixture.Game.ContentFile("img/pictures/added.png")));
        Assert.True(File.Exists(fixture.Game.ContentFile("mods/OldMod/plugin.js")));
        Assert.DoesNotContain(fixture.Vanilla.Files, file => file.StartsWith("mods/OldMod", StringComparison.Ordinal));
    }

    [Fact]
    public void GameUpdate_WithoutTheStore_InterruptedAtAnyStep_LeavesNoTrustedPartialBackup()
    {
        int calls = CountCalls(Prepare, Rebuild);
        Assert.True(calls > 3, $"Only {calls} destructive calls were counted.");

        for (int failAt = 1; failAt <= calls; failAt++)
        {
            using var fixture = new BaselineFixture();
            Prepare(fixture);
            var faulty = new FaultInjectingFileOps(failAt);

            Assert.Throws<IOException>(() => Rebuild(fixture, faulty));

            fixture.AssertNoBackupOrConsistent();
            Assert.True(Rebuild(fixture, new FileSystemOps()).Built, $"Rerun after a failure at {faulty.FailedCall}.");
            Assert.False(File.Exists(fixture.Game.ContentFile("img/pictures/added.png")));
            fixture.AssertNoBackupOrConsistent();
        }

        static void Prepare(BaselineFixture fixture)
        {
            Assert.True(fixture.Builder(new FileSystemOps(), new NoStoreManifestSource()).Build(BaselineAnswers.None).Built);
            fixture.InstallMod("AddMod", "img/pictures/added.png", "added by a mod");
            fixture.WriteLive("img/pictures/added.png", "added by a mod");
        }

        static BaselineOutcome Rebuild(BaselineFixture fixture, IFileSystemOps files)
        {
            return fixture.Builder(files, new NoStoreManifestSource()).Build(BaselineAnswers.None);
        }
    }

    // ---- TX-21: the audit of the weaker path ----

    [Fact]
    public void LiveBuild_AsksForConfirmation_WhenAFileEqualsAModFileInContentAndTime()
    {
        using var fixture = new BaselineFixture();
        string modCopy = fixture.InstallMod("TitleMod", "img/pictures/other.png", TitleContent);
        File.SetLastWriteTimeUtc(modCopy, File.GetLastWriteTimeUtc(fixture.Game.ContentFile(Title)));
        var files = new FileSystemOps();

        BaselineOutcome asked = fixture.Builder(files, new NoStoreManifestSource()).Build(BaselineAnswers.None);

        Assert.False(asked.Built);
        Assert.Equal(BaselineQuestion.ConfirmModMatches, asked.Question);
        Assert.Equal(1, asked.Audit!.SameContentAndTime);
        Assert.Contains(asked.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBackupMatchesMod && item.Severity == DiagnosticSeverity.Warning);
        Assert.False(fixture.Vanilla.IsComplete);

        BaselineOutcome confirmed = fixture.Builder(files, new NoStoreManifestSource()).Build(new BaselineAnswers(ModMatchesConfirmed: true));

        Assert.True(confirmed.Built, Describe(confirmed));
        Assert.Equal(1, fixture.Vanilla.ReadManifest()!.Audit!.SameContentAndTime);
    }

    [Fact]
    public void LiveBuild_WarnsWithoutAsking_WhenAFileEqualsAModFileInContentOnly()
    {
        using var fixture = new BaselineFixture();
        string modCopy = fixture.InstallMod("TitleMod", "img/pictures/other.png", TitleContent);
        File.SetLastWriteTimeUtc(modCopy, File.GetLastWriteTimeUtc(fixture.Game.ContentFile(Title)).AddMinutes(-5));
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Builder(files, new NoStoreManifestSource()).Build(BaselineAnswers.None);

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Equal(BaselineQuestion.None, outcome.Question);
        Assert.Equal(1, outcome.Audit!.SameContent);
        Assert.Equal(0, outcome.Audit.SameContentAndTime);
        Assert.Equal("TitleMod", Assert.Single(outcome.Audit.Mods).Mod);
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBackupMatchesMod);
    }

    [Fact]
    public void LiveBuild_RefusesADirtyInstall()
    {
        using var fixture = new BaselineFixture();
        fixture.WriteLive("index.html", "<script src=\"js/mods/mattieFMModLoader.js\"></script>");

        BaselineOutcome outcome = fixture.Builder(new FileSystemOps(), new NoStoreManifestSource()).Build(BaselineAnswers.None);

        Assert.False(outcome.Built);
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.FhmmInstalledOutside && item.Severity == DiagnosticSeverity.Error);
        Assert.False(fixture.Vanilla.IsComplete);
    }

    private static int CountCalls(Action<BaselineFixture> prepare, Func<BaselineFixture, IFileSystemOps, BaselineOutcome> run)
    {
        using var fixture = new BaselineFixture();
        prepare(fixture);
        var counter = new FaultInjectingFileOps();
        Assert.True(run(fixture, counter).Built);
        return counter.Calls;
    }

    private static string Describe(BaselineOutcome outcome)
    {
        return $"{outcome.Question}: " + string.Join("; ", outcome.Result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
    }
}
