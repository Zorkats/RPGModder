using System.Runtime.Versioning;
using Newtonsoft.Json;
using RPGModder.Core.Application;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;

namespace RPGModder.Tests;

// Slice 0b safety rules: the new-file collision guard (GR-M9), the backup marker (TX-6), mod removal
// through the trash (TX-17), the deployed state behind "pending changes" (UI-14) and the updater's
// timeout call.
public sealed class SafetyGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderSafety_{Guid.NewGuid():N}");
    private readonly string _game;
    private readonly string _executable;

    public SafetyGuardTests()
    {
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_game, "data"));
        Directory.CreateDirectory(Path.Combine(_game, "img", "pictures"));
        File.WriteAllText(Path.Combine(_game, "data", "Actors.json"), "[null]");
        File.WriteAllText(Path.Combine(_game, "img", "pictures", "Title.png"), "vanilla title");
        _executable = Path.Combine(_game, "Game.exe");
        File.WriteAllText(_executable, "test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Guard_DifferentBytesAtNewPath_RefusesBeforeWriting()
    {
        var engine = OpenEngine();
        var workspace = new GameWorkspacePaths(_game);
        WriteCopyMod(workspace, "mod-a", "img/pictures/New.png", "from a");
        WriteCopyMod(workspace, "mod-b", "img/pictures/New.png", "from b");

        OperationResult result = engine.RebuildGame(Profile("mod-a", "mod-b"));

        Assert.False(result.Success);
        OperationDiagnostic error = Assert.Single(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.Equal(DiagnosticCodes.DeployUnresolvedCollisions, error.Code);
        Assert.Contains("mod-a", error.Message);
        Assert.Contains("mod-b", error.Message);
        Assert.False(File.Exists(Path.Combine(_game, "img", "pictures", "New.png")));
    }

    [Fact]
    public void Guard_EqualBytesAtNewPath_Deploys()
    {
        var engine = OpenEngine();
        var workspace = new GameWorkspacePaths(_game);
        WriteCopyMod(workspace, "mod-a", "img/pictures/New.png", "same bytes");
        WriteCopyMod(workspace, "mod-b", "img/pictures/New.png", "same bytes");

        OperationResult result = engine.RebuildGame(Profile("mod-a", "mod-b"));

        Assert.True(result.Success, Describe(result));
        Assert.Equal("same bytes", File.ReadAllText(Path.Combine(_game, "img", "pictures", "New.png")));
    }

    [Fact]
    public void Guard_PathWithVanillaCounterpart_IsNotGuarded()
    {
        var engine = OpenEngine();
        var workspace = new GameWorkspacePaths(_game);
        WriteCopyMod(workspace, "mod-a", "img/pictures/Title.png", "title a");
        WriteCopyMod(workspace, "mod-b", "img/pictures/Title.png", "title b");

        OperationResult result = engine.RebuildGame(Profile("mod-a", "mod-b"));

        Assert.True(result.Success, Describe(result));
        Assert.Equal("title b", File.ReadAllText(Path.Combine(_game, "img", "pictures", "Title.png")));
    }

    [Fact]
    public void Open_UnmarkedBackupFolder_IsDiscardedAndTakenAgain()
    {
        var workspace = new GameWorkspacePaths(_game);
        string leftover = Path.Combine(workspace.CleanBackup, "data", "Leftover.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "from an interrupted backup");

        var engine = OpenEngine();

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(engine.BackupMarkerPath));
        Assert.Equal("[null]", File.ReadAllText(Path.Combine(workspace.CleanBackup, "data", "Actors.json")));
    }

    [Fact]
    public async Task Reset_BackupFolderCannotBeDeleted_LeavesNoTrustedBackupAndOpenTakesItAgain()
    {
        GameSession session = await OpenSessionAsync();
        string backupData = Path.Combine(session.Workspace.CleanBackup, "data");
        OperationResult result;

        if (OperatingSystem.IsWindows())
        {
            // An open handle without delete sharing makes the recursive delete fail on Windows.
            using (new FileStream(Path.Combine(backupData, "Actors.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = await new VanillaBackupWorkflow().ResetAsync(session);
            }
        }
        else
        {
            result = await ResetWithReadOnlyFolderAsync(session, backupData);
        }

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == DiagnosticCodes.WorkspaceBackupResetFailed);
        Assert.False(File.Exists(session.Engine.BackupMarkerPath));

        // The game changes before the next open; the backup taken then must describe the new game.
        File.WriteAllText(Path.Combine(_game, "data", "Actors.json"), "[null,{}]");
        GameSession reopened = await OpenSessionAsync();

        Assert.True(File.Exists(reopened.Engine.BackupMarkerPath));
        Assert.Equal("[null,{}]", File.ReadAllText(Path.Combine(backupData, "Actors.json")));
    }

    [Fact]
    public async Task Remove_MovesToTrashTakesModOutOfProfilesAndPurges()
    {
        GameSession session = await OpenSessionAsync();
        WriteCopyMod(session.Workspace, "mod-a", "img/pictures/New.png", "a");
        WriteCopyMod(session.Workspace, "mod-b", "img/pictures/Other.png", "b");
        var profiles = new ProfileRepository();
        profiles.Save(session.Workspace, "Default", Profile("mod-a", "mod-b"));
        profiles.Save(session.Workspace, "Second", Profile("mod-a"));

        OperationResult result = await new ModLibraryWorkflow().RemoveAsync(session, "mod-a");

        Assert.True(result.Success, Describe(result));
        Assert.Empty(result.Diagnostics);
        Assert.False(Directory.Exists(Path.Combine(session.Workspace.Mods, "mod-a")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(session.Workspace.Trash));
        Assert.Equal(new[] { "mod-b" }, profiles.Load(session.Workspace, "Default").EnabledMods);
        Assert.Equal(new[] { "mod-b" }, profiles.Load(session.Workspace, "Default").LoadOrder);
        Assert.Empty(profiles.Load(session.Workspace, "Second").EnabledMods);
    }

    [Fact]
    public async Task Remove_ProfilesCannotBeWritten_KeepsTheFolderInTheTrash()
    {
        GameSession session = await OpenSessionAsync();
        WriteCopyMod(session.Workspace, "mod-a", "img/pictures/New.png", "a");
        new ProfileRepository().Save(session.Workspace, "Default", Profile("mod-a"));
        var workflow = new ModLibraryWorkflow(new ModInstallerService(), new UnwritableProfiles(), TimeProvider.System);

        OperationResult result = await workflow.RemoveAsync(session, "mod-a");

        OperationDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.InstallRemoveFailed, error.Code);
        Assert.NotNull(error.Subject);
        Assert.True(File.Exists(Path.Combine(error.Subject!, "mod.json")), "The removed mod must be recoverable from the trash.");
        Assert.StartsWith(session.Workspace.Trash, error.Subject);
        Assert.False(Directory.Exists(Path.Combine(session.Workspace.Mods, "mod-a")));
    }

    [Fact]
    public async Task Deploy_RecordsState_AndPendingFollowsTheProfileAndItsFiles()
    {
        var sessions = new GameSessionService(new StoppedGame());
        GameSession session = await sessions.OpenAsync(_executable);
        WriteCopyMod(session.Workspace, "mod-a", "img/pictures/New.png", "a");
        WriteCopyMod(session.Workspace, "mod-b", "img/pictures/Other.png", "b");
        ModProfile profile = Profile("mod-a");

        Assert.True(await sessions.HasPendingChangesAsync(session, "Default", profile), "Nothing is deployed yet.");

        OperationResult deploy = await sessions.DeployAsync(session, "Default", profile);

        Assert.True(deploy.Success, Describe(deploy));
        Assert.True(File.Exists(session.Workspace.DeployedState));
        Assert.False(await sessions.HasPendingChangesAsync(session, "Default", profile));
        Assert.False(await sessions.HasPendingChangesAsync(session, "default", Profile("mod-a")));
        Assert.True(await sessions.HasPendingChangesAsync(session, "Second", profile), "Another profile is not deployed.");
        Assert.True(await sessions.HasPendingChangesAsync(session, "Default", Profile("mod-a", "mod-b")));

        File.WriteAllText(Path.Combine(session.Workspace.Mods, "mod-a", "payload.bin"), "a, edited");
        Assert.True(await sessions.HasPendingChangesAsync(session, "Default", profile), "A mod file changed after the deploy.");
    }

    [Fact]
    public async Task Reset_ForgetsTheDeployedState()
    {
        var sessions = new GameSessionService(new StoppedGame());
        GameSession session = await sessions.OpenAsync(_executable);
        WriteCopyMod(session.Workspace, "mod-a", "img/pictures/New.png", "a");
        ModProfile profile = Profile("mod-a");
        Assert.True((await sessions.DeployAsync(session, "Default", profile)).Success);

        OperationResult reset = await new VanillaBackupWorkflow().ResetAsync(session);

        Assert.True(reset.Success, Describe(reset));
        Assert.False(File.Exists(session.Workspace.DeployedState));
        Assert.False(File.Exists(Path.Combine(_game, "img", "pictures", "New.png")));
        Assert.True(await sessions.HasPendingChangesAsync(session, "Default", profile));
    }

    [Fact]
    public void WindowsUpdateScript_CallsTimeoutByFullPath()
    {
        string script = UpdateService.BuildWindowsUpdateScript("RPGModder.exe", @"C:\Temp\RPGModder_Backup_test");

        Assert.Contains("\"%SystemRoot%\\System32\\timeout.exe\" /t 2 /nobreak", script);
        Assert.DoesNotContain(script.Split('\n'), line => line.TrimStart().StartsWith("timeout ", StringComparison.OrdinalIgnoreCase));
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<OperationResult> ResetWithReadOnlyFolderAsync(GameSession session, string backupData)
    {
        // A folder without write permission keeps its entries, so the recursive delete fails.
        UnixFileMode mode = File.GetUnixFileMode(backupData);
        File.SetUnixFileMode(backupData, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            return await new VanillaBackupWorkflow().ResetAsync(session);
        }
        finally
        {
            File.SetUnixFileMode(backupData, mode);
        }
    }

    private ModEngine OpenEngine()
    {
        var engine = new ModEngine(_executable);
        engine.InitializeSafeState();
        return engine;
    }

    private Task<GameSession> OpenSessionAsync()
    {
        return new GameSessionService(new StoppedGame()).OpenAsync(_executable);
    }

    private static ModProfile Profile(params string[] enabledMods)
    {
        return new ModProfile { EnabledMods = enabledMods.ToList(), LoadOrder = enabledMods.ToList() };
    }

    private static string Describe(OperationResult result)
    {
        return string.Join("; ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
    }

    private static void WriteCopyMod(GameWorkspacePaths workspace, string id, string target, string content)
    {
        string mod = Path.Combine(workspace.Mods, id);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "payload.bin"), content);
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Id = id, Name = id },
            FileOps = new List<FileOperation> { new() { Source = "payload.bin", Target = target } },
        };
        File.WriteAllText(Path.Combine(mod, "mod.json"), JsonConvert.SerializeObject(manifest));
    }

    private sealed class StoppedGame : IGameProcessProbe
    {
        public bool IsRunning(string executablePath)
        {
            return false;
        }
    }

    // Reads like the real repository and fails every write, as a full disk or a locked file would.
    private sealed class UnwritableProfiles : IProfileRepository
    {
        private readonly ProfileRepository _inner = new();

        public IReadOnlyList<string> List(GameWorkspacePaths workspace)
        {
            return _inner.List(workspace);
        }

        public ModProfile Load(GameWorkspacePaths workspace, string profileName)
        {
            return _inner.Load(workspace, profileName);
        }

        public void Save(GameWorkspacePaths workspace, string profileName, ModProfile profile)
        {
            throw new IOException("The profile file is locked.");
        }

        public void Delete(GameWorkspacePaths workspace, string profileName)
        {
            throw new IOException("The profile file is locked.");
        }

        public string ReadActive(GameWorkspacePaths workspace)
        {
            return _inner.ReadActive(workspace);
        }

        public void WriteActive(GameWorkspacePaths workspace, string profileName)
        {
            throw new IOException("The marker file is locked.");
        }
    }
}
