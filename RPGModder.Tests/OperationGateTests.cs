using System.Diagnostics;
using Newtonsoft.Json;
using RPGModder.Core.Application;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Platform;
using RPGModder.Core.Services;
using Xunit;
using RPGModder.Core.Workspace;

namespace RPGModder.Tests;

public sealed class OperationGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _game;
    private readonly string _executable;

    public OperationGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RPGModderGateTests_{Guid.NewGuid():N}");
        _game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(_game, "data"));
        File.WriteAllText(Path.Combine(_game, "data", "Actors.json"), "[null]");
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
    public void CON3_TryEnter_NeverWaits_SecondCallerRefusedWithOperationBusyNamingRunningOperation()
    {
        string lockPath = Path.Combine(_root, "con3_nowait", "operation.lock");
        var gate = new OperationGate(lockPath);

        bool enteredFirst = gate.TryEnter("First operation", out IDisposable? lease1, out OperationDiagnostic? refusal1);
        Assert.True(enteredFirst);
        Assert.NotNull(lease1);
        Assert.Null(refusal1);
        Assert.True(gate.IsBusy);
        Assert.Equal("First operation", gate.CurrentOperation);

        var stopwatch = Stopwatch.StartNew();
        bool enteredSecond = gate.TryEnter("Second operation", out IDisposable? lease2, out OperationDiagnostic? refusal2);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500);
        Assert.False(enteredSecond);
        Assert.Null(lease2);
        Assert.NotNull(refusal2);
        Assert.Equal(DiagnosticCodes.OperationBusy, refusal2.Code);
        Assert.Equal(DiagnosticSeverity.Error, refusal2.Severity);
        Assert.Equal("First operation", refusal2.Subject);
        Assert.Contains("First operation", refusal2.Message);
        Assert.True(gate.IsBusy);
        Assert.Equal("First operation", gate.CurrentOperation);

        lease1.Dispose();
        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);

        bool enteredThird = gate.TryEnter("Third operation", out IDisposable? lease3, out OperationDiagnostic? refusal3);
        Assert.True(enteredThird);
        Assert.NotNull(lease3);
        Assert.Null(refusal3);
        Assert.True(gate.IsBusy);
        Assert.Equal("Third operation", gate.CurrentOperation);

        lease3.Dispose();
        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);
    }

    [Fact]
    public void CON3_Changed_FiresOnEnterAndOnLeave()
    {
        string lockPath = Path.Combine(_root, "con3_events", "operation.lock");
        var gate = new OperationGate(lockPath);
        int changedCount = 0;
        gate.Changed += delegate (object? sender, EventArgs args)
        {
            changedCount++;
        };

        bool enteredFirst = gate.TryEnter("Operation A", out IDisposable? lease1, out _);
        Assert.True(enteredFirst);
        Assert.Equal(1, changedCount);

        bool enteredSecond = gate.TryEnter("Operation B", out IDisposable? lease2, out _);
        Assert.False(enteredSecond);
        Assert.Null(lease2);
        Assert.Equal(1, changedCount);

        lease1!.Dispose();
        Assert.Equal(2, changedCount);

        bool enteredThird = gate.TryEnter("Operation C", out IDisposable? lease3, out _);
        Assert.True(enteredThird);
        Assert.Equal(3, changedCount);

        lease3!.Dispose();
        Assert.Equal(4, changedCount);
    }

    [Fact]
    public void CON3_Refused_WrapsRefusalIntoFailedOperationResult()
    {
        var diagnostic = new OperationDiagnostic(
            DiagnosticCodes.OperationBusy,
            "Busy message",
            DiagnosticSeverity.Error,
            "Subject");

        OperationResult result = OperationGate.Refused(diagnostic);

        Assert.False(result.Success);
        OperationDiagnostic single = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OperationBusy, single.Code);
        Assert.Equal("Busy message", single.Message);
        Assert.Equal(DiagnosticSeverity.Error, single.Severity);
        Assert.Equal("Subject", single.Subject);
    }

    [Fact]
    public void CON3_TryEnter_TwoArgumentOverload_BehavesIdentically()
    {
        string lockPath = Path.Combine(_root, "con3_overload", "operation.lock");
        var gate = new OperationGate(lockPath);

        bool enteredFirst = gate.TryEnter("Primary operation", out IDisposable? lease1);
        Assert.True(enteredFirst);
        Assert.NotNull(lease1);
        Assert.True(gate.IsBusy);

        bool enteredSecond = gate.TryEnter("Secondary operation", out IDisposable? lease2);
        Assert.False(enteredSecond);
        Assert.Null(lease2);

        lease1.Dispose();
        Assert.False(gate.IsBusy);

        bool enteredThird = gate.TryEnter("Tertiary operation", out IDisposable? lease3);
        Assert.True(enteredThird);
        Assert.NotNull(lease3);

        lease3.Dispose();
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public void CON5_TwoGatesOnSameLockPath_SecondCallerRefusedWhileFirstHoldsLease()
    {
        string lockPath = Path.Combine(_root, "con5_two_gates", ".rpgmodder", "operation.lock");
        var gate1 = new OperationGate(lockPath);
        var gate2 = new OperationGate(lockPath);

        bool entered1 = gate1.TryEnter("Process 1 Op", out IDisposable? lease1, out OperationDiagnostic? refusal1);
        Assert.True(entered1);
        Assert.NotNull(lease1);
        Assert.Null(refusal1);

        bool entered2 = gate2.TryEnter("Process 2 Op", out IDisposable? lease2, out OperationDiagnostic? refusal2);
        Assert.False(entered2);
        Assert.Null(lease2);
        Assert.NotNull(refusal2);
        Assert.Equal(DiagnosticCodes.OperationBusy, refusal2.Code);
        Assert.Equal(DiagnosticSeverity.Error, refusal2.Severity);
        Assert.Equal("Another RPGModder process is working on this game.", refusal2.Message);
        Assert.Equal(Path.GetFullPath(lockPath), refusal2.Subject);

        lease1.Dispose();
        lease1.Dispose();

        bool entered2AfterRelease = gate2.TryEnter("Process 2 Op", out lease2, out refusal2);
        Assert.True(entered2AfterRelease);
        Assert.NotNull(lease2);
        Assert.Null(refusal2);

        lease2.Dispose();
    }

    [Fact]
    public void CON5_LeftoverLockFileOnDisk_DoesNotBlockTryEnter()
    {
        string lockPath = Path.Combine(_root, "con5_stale", ".rpgmodder", "operation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, "dead process unheld lock");
        Assert.True(File.Exists(lockPath));

        var gate = new OperationGate(lockPath);
        bool entered = gate.TryEnter("Fresh operation", out IDisposable? lease, out OperationDiagnostic? refusal);
        Assert.True(entered);
        Assert.NotNull(lease);
        Assert.Null(refusal);

        lease.Dispose();
    }

    [Fact]
    public async Task CON5_SessionOpen_LeftoverLockFileFromDeadProcess_IsClearedAtSessionOpen()
    {
        string lockPath = Path.Combine(_game, ".rpgmodder", "operation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, "stale content from dead process");
        Assert.True(File.Exists(lockPath));

        var sessions = new GameSessionService(new FakeGameProcessProbe(false));
        GameSession session = await sessions.OpenAsync(_executable);

        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);

        bool entered = session.Gate.TryEnter("Workflow after open", out IDisposable? lease, out OperationDiagnostic? refusal);
        Assert.True(entered);
        Assert.NotNull(lease);
        Assert.Null(refusal);

        lease.Dispose();
    }

    [Fact]
    public void CON5_Lease_DisposeTwice_IsHarmless()
    {
        string lockPath = Path.Combine(_root, "con5_double_dispose", "operation.lock");
        var gate = new OperationGate(lockPath);

        bool entered = gate.TryEnter("Test op", out IDisposable? lease, out _);
        Assert.True(entered);
        Assert.NotNull(lease);

        lease.Dispose();
        lease.Dispose();

        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task CON4_DeployAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        WriteCopyMod(session.Workspace, "mod-a", "data/Actors.json", "modded actors");
        var profile = new ModProfile
        {
            EnabledMods = new List<string> { "mod-a" },
            LoadOrder = new List<string> { "mod-a" }
        };

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            var sessions = new GameSessionService(session.GameProcess);
            OperationResult result = await sessions.DeployAsync(session, "Default", profile);

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.Equal("[null]", File.ReadAllText(Path.Combine(_game, "data", "Actors.json")));
            Assert.False(File.Exists(session.Workspace.DeployedState));
        }
    }

    [Fact]
    public async Task CON4_ProfileSwitchAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "default profile save");

        var profiles = new ProfileStore();
        profiles.Save(session.Workspace, "Default", new ModProfile());
        profiles.Save(session.Workspace, "Second", new ModProfile());
        profiles.WriteActive(session.Workspace, "Default");

        string secondSaves = Path.Combine(session.Workspace.ProfileSaves, "Second");
        Directory.CreateDirectory(secondSaves);
        File.WriteAllText(Path.Combine(secondSaves, "file1.rpgsave"), "second profile save");

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new ProfileWorkflow().SwitchAsync(session, "Default", "Second");

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.Equal("default profile save", File.ReadAllText(saveFile));
            Assert.Equal("Default", profiles.ReadActive(session.Workspace));
        }
    }

    [Fact]
    public async Task CON4_ProfileCreateAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        string newProfilePath = session.Workspace.GetProfilePath("NewProfile");
        Assert.False(File.Exists(newProfilePath));

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new ProfileWorkflow().CreateAsync(session, "NewProfile", new ModProfile());

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.False(File.Exists(newProfilePath));
        }
    }

    [Fact]
    public async Task CON4_ModRemoveAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        string modFolder = Path.Combine(session.Workspace.Mods, "mod-to-remove");
        Directory.CreateDirectory(modFolder);
        File.WriteAllText(Path.Combine(modFolder, "mod.json"), "{}");

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new ModLibraryWorkflow().RemoveAsync(session, "mod-to-remove");

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.True(Directory.Exists(modFolder));
            Assert.True(!Directory.Exists(session.Workspace.Trash) || !Directory.EnumerateFileSystemEntries(session.Workspace.Trash).Any());
        }
    }

    [Fact]
    public async Task CON4_SaveBackupAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "save file content");

        int initialBackups = Directory.Exists(session.Workspace.SaveBackups)
            ? Directory.GetFiles(session.Workspace.SaveBackups).Length
            : 0;

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new SaveBackupWorkflow().BackupAsync(session, saveFile);

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            int afterBackups = Directory.Exists(session.Workspace.SaveBackups)
                ? Directory.GetFiles(session.Workspace.SaveBackups).Length
                : 0;
            Assert.Equal(initialBackups, afterBackups);
        }
    }

    [Fact]
    public async Task CON4_SaveRestoreAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "live save content");

        Directory.CreateDirectory(session.Workspace.SaveBackups);
        string backupFile = Path.Combine(session.Workspace.SaveBackups, "file1_backup.bak");
        File.WriteAllText(backupFile, "backup file content");

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new SaveBackupWorkflow().RestoreAsync(session, saveFile, backupFile);

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.Equal("live save content", File.ReadAllText(saveFile));
            Assert.False(File.Exists(Path.Combine(session.Workspace.SaveBackups, "file1.rpgsave_PRE_RESTORE.bak")));
        }
    }

    [Fact]
    public async Task CON4_VanillaBackupResetAsync_WhenGateIsHeld_RefusesWithOperationBusyAndLeavesDiskUntouched()
    {
        GameSession session = await OpenSessionAsync(false);
        Assert.True(File.Exists(session.Engine.BackupMarkerPath));
        Assert.True(Directory.Exists(session.Workspace.CleanBackup));

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            OperationResult result = await new VanillaBackupWorkflow().ResetAsync(session);

            Assert.False(result.Success);
            OperationDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticCodes.OperationBusy, error.Code);
            Assert.True(File.Exists(session.Engine.BackupMarkerPath));
            Assert.True(Directory.Exists(session.Workspace.CleanBackup));
        }
    }

    [Fact]
    public async Task CON4_AllWorkflows_WhenGateIsHeld_RefuseWithOperationBusyAndChangeNothingOnDisk()
    {
        GameSession session = await OpenSessionAsync(false);

        WriteCopyMod(session.Workspace, "mod-deploy", "data/Actors.json", "modded actors");
        var profile = new ModProfile
        {
            EnabledMods = new List<string> { "mod-deploy" },
            LoadOrder = new List<string> { "mod-deploy" }
        };

        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "default profile save");

        var profiles = new ProfileStore();
        profiles.Save(session.Workspace, "Default", new ModProfile());
        profiles.Save(session.Workspace, "Second", new ModProfile());
        profiles.WriteActive(session.Workspace, "Default");

        string secondSaves = Path.Combine(session.Workspace.ProfileSaves, "Second");
        Directory.CreateDirectory(secondSaves);
        File.WriteAllText(Path.Combine(secondSaves, "file1.rpgsave"), "second profile save");

        string modToRemove = Path.Combine(session.Workspace.Mods, "mod-to-remove");
        Directory.CreateDirectory(modToRemove);
        File.WriteAllText(Path.Combine(modToRemove, "mod.json"), "{}");

        Directory.CreateDirectory(session.Workspace.SaveBackups);
        string backupFile = Path.Combine(session.Workspace.SaveBackups, "file1_backup.bak");
        File.WriteAllText(backupFile, "backup file content");

        string newProfilePath = session.Workspace.GetProfilePath("NewProfile");

        bool held = session.Gate.TryEnter("Test holder", out IDisposable? lease, out _);
        Assert.True(held);
        using (lease)
        {
            // 1. Deploy
            var sessions = new GameSessionService(session.GameProcess);
            OperationResult deployResult = await sessions.DeployAsync(session, "Default", profile);
            Assert.False(deployResult.Success);
            Assert.Contains(deployResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.Equal("[null]", File.ReadAllText(Path.Combine(_game, "data", "Actors.json")));
            Assert.False(File.Exists(session.Workspace.DeployedState));

            // 2. Profile switch
            OperationResult switchResult = await new ProfileWorkflow().SwitchAsync(session, "Default", "Second");
            Assert.False(switchResult.Success);
            Assert.Contains(switchResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.Equal("default profile save", File.ReadAllText(saveFile));
            Assert.Equal("Default", profiles.ReadActive(session.Workspace));

            // 3. Profile create
            OperationResult createResult = await new ProfileWorkflow().CreateAsync(session, "NewProfile", new ModProfile());
            Assert.False(createResult.Success);
            Assert.Contains(createResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.False(File.Exists(newProfilePath));

            // 4. Mod remove
            OperationResult removeResult = await new ModLibraryWorkflow().RemoveAsync(session, "mod-to-remove");
            Assert.False(removeResult.Success);
            Assert.Contains(removeResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.True(Directory.Exists(modToRemove));

            // 5. Save backup
            int backupsBefore = Directory.GetFiles(session.Workspace.SaveBackups).Length;
            OperationResult backupResult = await new SaveBackupWorkflow().BackupAsync(session, saveFile);
            Assert.False(backupResult.Success);
            Assert.Contains(backupResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.Equal(backupsBefore, Directory.GetFiles(session.Workspace.SaveBackups).Length);

            // 6. Save restore
            OperationResult restoreResult = await new SaveBackupWorkflow().RestoreAsync(session, saveFile, backupFile);
            Assert.False(restoreResult.Success);
            Assert.Contains(restoreResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.Equal("default profile save", File.ReadAllText(saveFile));

            // 7. Backup reset
            OperationResult resetResult = await new VanillaBackupWorkflow().ResetAsync(session);
            Assert.False(resetResult.Success);
            Assert.Contains(resetResult.Diagnostics, delegate (OperationDiagnostic d)
            {
                return d.Code == DiagnosticCodes.OperationBusy;
            });
            Assert.True(File.Exists(session.Engine.BackupMarkerPath));
            Assert.True(Directory.Exists(session.Workspace.CleanBackup));
        }

        Assert.False(session.Gate.IsBusy);
    }

    [Fact]
    public async Task CON6_DeployAsync_WhenGameRunning_RefusesWithGameRunningAndReleasesGate()
    {
        GameSession session = await OpenSessionAsync(true);
        var sessions = new GameSessionService(session.GameProcess);

        OperationResult result = await sessions.DeployAsync(session, "Default", new ModProfile());

        Assert.False(result.Success);
        OperationDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.GameRunning, error.Code);
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
    }

    [Fact]
    public async Task CON6_ProfileSwitchAsync_WhenGameRunning_RefusesWithGameRunningAndReleasesGate()
    {
        GameSession session = await OpenSessionAsync(true);

        OperationResult result = await new ProfileWorkflow().SwitchAsync(session, "Default", "Second");

        Assert.False(result.Success);
        OperationDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.GameRunning, error.Code);
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
    }

    [Fact]
    public async Task CON6_SaveRestoreAsync_WhenGameRunning_RefusesWithGameRunningAndReleasesGate()
    {
        GameSession session = await OpenSessionAsync(true);
        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "live save");

        Directory.CreateDirectory(session.Workspace.SaveBackups);
        string backupFile = Path.Combine(session.Workspace.SaveBackups, "file1.bak");
        File.WriteAllText(backupFile, "backup save");

        OperationResult result = await new SaveBackupWorkflow().RestoreAsync(session, saveFile, backupFile);

        Assert.False(result.Success);
        OperationDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.GameRunning, error.Code);
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
        Assert.Equal("live save", File.ReadAllText(saveFile));
    }

    [Fact]
    public async Task CON6_VanillaBackupResetAsync_WhenGameRunning_RefusesWithGameRunningAndReleasesGate()
    {
        GameSession session = await OpenSessionAsync(true);

        OperationResult result = await new VanillaBackupWorkflow().ResetAsync(session);

        Assert.False(result.Success);
        OperationDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.GameRunning, error.Code);
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
        Assert.True(File.Exists(session.Engine.BackupMarkerPath));
    }

    [Fact]
    public async Task CON6_ProfileCreateAsync_WhenGameRunning_SucceedsBecauseWorkspaceOnly()
    {
        GameSession session = await OpenSessionAsync(true);
        string newProfilePath = session.Workspace.GetProfilePath("WorkspaceOnlyProfile");

        OperationResult result = await new ProfileWorkflow().CreateAsync(session, "WorkspaceOnlyProfile", new ModProfile());

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
        Assert.True(File.Exists(newProfilePath));
    }

    [Fact]
    public async Task CON6_SaveBackupAsync_WhenGameRunning_SucceedsBecauseWorkspaceOnly()
    {
        GameSession session = await OpenSessionAsync(true);
        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "live save to backup");

        OperationResult result = await new SaveBackupWorkflow().BackupAsync(session, saveFile);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);
        Assert.Null(session.Gate.CurrentOperation);
        Assert.True(Directory.Exists(session.Workspace.SaveBackups));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(session.Workspace.SaveBackups));
    }

    [Fact]
    public async Task CON6_AllWorkflows_WhenGameRunning_MutatingRefuseAndNonMutatingSucceed()
    {
        GameSession session = await OpenSessionAsync(true);

        string saveFolder = Path.Combine(_game, "save");
        Directory.CreateDirectory(saveFolder);
        string saveFile = Path.Combine(saveFolder, "file1.rpgsave");
        File.WriteAllText(saveFile, "live save content");

        Directory.CreateDirectory(session.Workspace.SaveBackups);
        string backupFile = Path.Combine(session.Workspace.SaveBackups, "file1.bak");
        File.WriteAllText(backupFile, "backup save content");

        // Mutating workflows: all refuse with GameRunning and leave gate free
        var sessions = new GameSessionService(session.GameProcess);
        OperationResult deployResult = await sessions.DeployAsync(session, "Default", new ModProfile());
        Assert.False(deployResult.Success);
        Assert.Contains(deployResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);

        OperationResult switchResult = await new ProfileWorkflow().SwitchAsync(session, "Default", "Second");
        Assert.False(switchResult.Success);
        Assert.Contains(switchResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);

        OperationResult restoreResult = await new SaveBackupWorkflow().RestoreAsync(session, saveFile, backupFile);
        Assert.False(restoreResult.Success);
        Assert.Contains(restoreResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);

        OperationResult resetResult = await new VanillaBackupWorkflow().ResetAsync(session);
        Assert.False(resetResult.Success);
        Assert.Contains(resetResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);

        // Non-mutating workflows: succeed and leave gate free
        OperationResult createResult = await new ProfileWorkflow().CreateAsync(session, "AllowedProfile", new ModProfile());
        Assert.True(createResult.Success);
        Assert.DoesNotContain(createResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);

        OperationResult backupResult = await new SaveBackupWorkflow().BackupAsync(session, saveFile);
        Assert.True(backupResult.Success);
        Assert.DoesNotContain(backupResult.Diagnostics, delegate (OperationDiagnostic d)
        {
            return d.Code == DiagnosticCodes.GameRunning;
        });
        Assert.False(session.Gate.IsBusy);
    }

    private Task<GameSession> OpenSessionAsync(bool isGameRunning)
    {
        var probe = new FakeGameProcessProbe(isGameRunning);
        return new GameSessionService(probe).OpenAsync(_executable);
    }

    private static void WriteCopyMod(GameWorkspacePaths workspace, string id, string target, string content)
    {
        string mod = Path.Combine(workspace.Mods, id);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "payload.bin"), content);
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata
            {
                Id = id,
                Name = id
            },
            FileOps = new List<FileOperation>
            {
                new()
                {
                    Source = "payload.bin",
                    Target = target
                }
            }
        };
        File.WriteAllText(Path.Combine(mod, "mod.json"), JsonConvert.SerializeObject(manifest));
    }

    private sealed class FakeGameProcessProbe : IGameProcessProbe
    {
        private readonly bool _isRunning;

        public FakeGameProcessProbe(bool isRunning)
        {
            _isRunning = isRunning;
        }

        public bool IsRunning(string executablePath)
        {
            return _isRunning;
        }
    }
}
