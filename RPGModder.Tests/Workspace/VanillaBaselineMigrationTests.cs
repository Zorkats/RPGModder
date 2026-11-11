using RPGModder.Core.Application;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

/// <summary>
/// Workspace migration 3 to 4 (GR-V6): a 2.0.0 backup and its marker become a manifest that never
/// claims more than can be proven (TST-11, TST-19). The 2.0.0 fixture is built in code, because a
/// stored fixture would lose the file times the audit compares.
/// </summary>
public sealed class VanillaBaselineMigrationTests
{
    private const string Title = "img/pictures/title.png";
    private const string TitleContent = "title picture";
    private static readonly DateTimeOffset MarkerDate = new(2025, 3, 1, 9, 0, 0, TimeSpan.Zero);

    // ---- TST-11: a dirty 2.0.0 backup without the store's list ----

    [Fact]
    public void DirtyLegacyBackup_WithoutTheStore_BecomesUnverified_WithExtrasMovedAndTheAudit()
    {
        using var fixture = new BaselineFixture();
        WriteDirtyLegacyWorkspace(fixture);

        BaselineOutcome outcome = fixture.Migration(new FileSystemOps(), new NoStoreManifestSource()).Run();

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Unverified, manifest.Baseline);
        Assert.True(manifest.BlocksDeploy);
        Assert.Equal(MarkerDate, manifest.CreatedUtc);
        Assert.Null(manifest.SteamAppId);
        Assert.Null(manifest.SteamBuildId);
        Assert.False(File.Exists(fixture.Vanilla.MarkerPath));
        Assert.Equal(GameWorkspacePaths.CurrentSchemaVersion, WorkspaceMigrationService.ReadSchemaVersion(fixture.Workspace));

        Assert.True(File.Exists(Path.Combine(fixture.Workspace.LegacyBackupExtras, "_moddata", "state.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Workspace.LegacyBackupExtras, "mods", "OldMod", "plugin.js")));
        Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("_moddata/", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("mods/OldMod/", StringComparison.Ordinal));
        Assert.Contains(manifest.Files, file => file.Path == "mods/readme.txt");
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBackupExtrasMoved);
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceStoreManifestUnavailable);

        Assert.Equal(1, manifest.Audit!.SameContentAndTime);
        Assert.Equal("TitleMod", Assert.Single(manifest.Audit.Mods).Mod);
        Assert.Contains(outcome.Result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBackupMatchesMod && item.Severity == DiagnosticSeverity.Warning);
        fixture.AssertNoBackupOrConsistent();
    }

    [Fact]
    public void DirtyLegacyBackup_MigratedTwice_ChangesNothingTheSecondTime()
    {
        using var fixture = new BaselineFixture();
        WriteDirtyLegacyWorkspace(fixture);
        Assert.True(fixture.Migration(new FileSystemOps(), new NoStoreManifestSource()).Run().Built);
        string manifestBefore = File.ReadAllText(fixture.Vanilla.ManifestPath);
        IReadOnlyList<string> filesBefore = fixture.Vanilla.Files;

        BaselineOutcome second = fixture.Migration(new FileSystemOps(), new NoStoreManifestSource()).Run();

        Assert.False(second.Built);
        Assert.Empty(second.Result.Diagnostics);
        Assert.Equal(manifestBefore, File.ReadAllText(fixture.Vanilla.ManifestPath));
        Assert.Equal(filesBefore, fixture.Vanilla.Files);
    }

    [Fact]
    public async Task DirtyLegacyBackup_RefusesDeploys_UntilThePlayerKeepsIt_ThenWarnsOnEach()
    {
        using var fixture = new BaselineFixture();
        WriteDirtyLegacyWorkspace(fixture);
        var sessions = new GameSessionService(new StoppedGame(), new FileSystemOps(), new NoStoreManifestSource(), TimeProvider.System);
        GameSession session = await sessions.OpenAsync(fixture.Game.ExecutablePath);
        Assert.Equal(VanillaBaseline.Unverified, fixture.Vanilla.ReadManifest()!.Baseline);

        OperationResult refused = await sessions.DeployAsync(session, "Default", EmptyProfile());

        Assert.False(refused.Success);
        Assert.Contains(refused.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBaselineUnverified && item.Severity == DiagnosticSeverity.Error);

        OperationResult kept = await new VanillaBackupWorkflow().KeepAsync(session);
        Assert.True(kept.Success, Describe(kept));
        OperationResult deployed = await sessions.DeployAsync(session, "Default", EmptyProfile());

        Assert.True(deployed.Success, Describe(deployed));
        Assert.Contains(deployed.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBaselineUnverified && item.Severity == DiagnosticSeverity.Warning);
    }

    // ---- TST-19: with the store's list the migrated backup is proven ----

    [Fact]
    public void LegacyBackup_ThatTheStoreProves_IsConfirmedWithoutAQuestion_AndExtrasMoveAside()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        WriteLegacyWorkspace(fixture);
        WriteBackupFile(fixture, "_moddata/state.json", "{\"seen\":true}");
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Migration(files, fixture.Steam(files)).Run();

        Assert.True(outcome.Built, Describe(outcome));
        Assert.Equal(BaselineQuestion.None, outcome.Question);
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.Equal(VanillaBaseline.StoreManifest, manifest.BaselineSource);
        Assert.Equal(SyntheticSteamLibrary.AppId, manifest.SteamAppId);
        Assert.Equal(SyntheticSteamLibrary.BuildId, manifest.SteamBuildId);
        Assert.Equal(SyntheticSteamLibrary.ManifestId, Assert.Single(manifest.SteamDepots!).ManifestId);
        Assert.Equal(MarkerDate, manifest.CreatedUtc);
        Assert.True(File.Exists(Path.Combine(fixture.Workspace.LegacyBackupExtras, "_moddata", "state.json")));
        Assert.False(File.Exists(fixture.Vanilla.MarkerPath));
        fixture.AssertNoBackupOrConsistent();
    }

    [Fact]
    public void LegacyBackup_WithAFileTheStoreDoesNotProve_GoesToTheRebuild()
    {
        using var fixture = new BaselineFixture();
        fixture.PublishStoreManifest();
        WriteLegacyWorkspace(fixture);
        WriteBackupFile(fixture, Title, "a modded title copied into the backup");
        var files = new FileSystemOps();

        BaselineOutcome outcome = fixture.Migration(files, fixture.Steam(files)).Run();

        Assert.True(outcome.Built, Describe(outcome));
        VanillaManifest manifest = fixture.Vanilla.ReadManifest()!;
        Assert.Equal(VanillaBaseline.Confirmed, manifest.Baseline);
        Assert.NotEqual(MarkerDate, manifest.CreatedUtc);
        Assert.Equal(TitleContent, fixture.Vanilla.ReadText(Title));
        Assert.False(File.Exists(fixture.Vanilla.MarkerPath));
        Assert.Equal(GameWorkspacePaths.CurrentSchemaVersion, WorkspaceMigrationService.ReadSchemaVersion(fixture.Workspace));
        fixture.AssertNoBackupOrConsistent();
    }

    // A 2.0.0 workspace at schema 3: a copy of the managed content and the completion marker.
    private static void WriteLegacyWorkspace(BaselineFixture fixture)
    {
        foreach (string file in ManagedContent.List(fixture.Game.ContentRoot))
        {
            string destination = Path.Combine(fixture.Workspace.CleanBackup, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(fixture.Game.ContentFile(file), destination);
        }

        File.WriteAllText(
            fixture.Vanilla.MarkerPath,
            "{\n  \"BackupDate\": \"2025-03-01T10:00:00+01:00\",\n  \"UsesWwwFolder\": false,\n  \"ContentPath\": \"\"\n}");
        File.WriteAllText(fixture.Workspace.SchemaMarker, "{\"schemaVersion\":3,\"migratedUtc\":\"2025-03-01T09:00:00Z\"}");
    }

    // The dirty 2.0.0 backup: extras the old code copied in, and a file copied from an installed mod.
    private static void WriteDirtyLegacyWorkspace(BaselineFixture fixture)
    {
        WriteLegacyWorkspace(fixture);
        WriteBackupFile(fixture, "_moddata/state.json", "{\"seen\":true}");
        WriteBackupFile(fixture, "mods/OldMod/mod.json", "{\"Metadata\":{\"Id\":\"OldMod\"}}");
        WriteBackupFile(fixture, "mods/OldMod/plugin.js", "// old library entry");
        WriteBackupFile(fixture, "mods/readme.txt", "shipped with the game");

        string modCopy = fixture.InstallMod("TitleMod", "img/pictures/modded.png", "a modded title");
        WriteBackupFile(fixture, Title, "a modded title");
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Workspace.CleanBackup, Title), File.GetLastWriteTimeUtc(modCopy));
    }

    private static void WriteBackupFile(BaselineFixture fixture, string relativePath, string content)
    {
        string path = Path.Combine(fixture.Workspace.CleanBackup, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static ModProfile EmptyProfile()
    {
        return new ModProfile { EnabledMods = new List<string>(), LoadOrder = new List<string>() };
    }

    private static string Describe(BaselineOutcome outcome)
    {
        return $"{outcome.Question}: " + Describe(outcome.Result);
    }

    private static string Describe(OperationResult result)
    {
        return string.Join("; ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
    }

    private sealed class StoppedGame : IGameProcessProbe
    {
        public bool IsRunning(string executablePath)
        {
            return false;
        }
    }
}
