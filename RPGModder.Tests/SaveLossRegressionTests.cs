using System.Diagnostics;
using System.Runtime.Versioning;
using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using RPGModder.Core.Workspace;

namespace RPGModder.Tests;

// Regression tests for failure paths that used to delete or lose save files.
public sealed class SaveLossRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RPGModderSaveLoss_{Guid.NewGuid():N}");

    public SaveLossRegressionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Transaction_RollbackAfterCommit_LeavesLiveFilesUntouched()
    {
        string content = Path.Combine(_root, "content");
        string data = Path.Combine(content, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Actors.json"), "before");

        var transaction = DeploymentTransaction.Begin(
            content,
            Path.Combine(_root, "transactions"),
            new[] { "data" },
            Array.Empty<string>());
        File.WriteAllText(Path.Combine(data, "Actors.json"), "deployed");
        transaction.Commit();

        transaction.Rollback();

        Assert.Equal("deployed", File.ReadAllText(Path.Combine(data, "Actors.json")));
    }

    [Fact]
    public void RecoverInterrupted_ApplyingJournalWithoutSnapshot_DoesNotDeleteLiveFiles()
    {
        string content = Path.Combine(_root, "content");
        string save = Path.Combine(content, "save");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "file1.rpgsave"), "progress");
        string transactions = Path.Combine(_root, "transactions");
        string orphan = Path.Combine(transactions, "deployment-orphan");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(
            Path.Combine(orphan, "transaction.json"),
            JsonConvert.SerializeObject(new { Id = "deployment-orphan", State = "applying", UpdatedUtc = DateTime.UtcNow }));

        DeploymentTransaction.RecoverInterrupted(content, transactions, new[] { "save" }, Array.Empty<string>());

        Assert.Equal("progress", File.ReadAllText(Path.Combine(save, "file1.rpgsave")));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void RebuildGame_HistoryWriteFailure_KeepsCommittedDeploymentAndSaves()
    {
        (string game, ModEngine engine, GameWorkspacePaths workspace) = CreateGame();
        string save = Path.Combine(game, "save");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "file1.rpgsave"), "progress");
        WriteCopyMod(workspace, "good-mod", "data/Actors.json", "modded");

        // A file where the history folder should be makes every history write fail.
        Directory.Delete(workspace.History, true);
        File.WriteAllText(workspace.History, "not a folder");

        OperationResult result = engine.RebuildGame(new ModProfile
        {
            EnabledMods = new List<string> { "good-mod" },
            LoadOrder = new List<string> { "good-mod" }
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, item => item.Code == "history.write_failed");
        Assert.Equal("modded", File.ReadAllText(Path.Combine(game, "data", "Actors.json")));
        Assert.Equal("progress", File.ReadAllText(Path.Combine(save, "file1.rpgsave")));
    }

    [Fact]
    public void SwapSaveFiles_CopyFailure_KeepsEveryLiveSave()
    {
        (string game, ModEngine engine, GameWorkspacePaths workspace) = CreateGame();
        string save = Path.Combine(game, "save");
        Directory.CreateDirectory(save);
        for (int i = 1; i <= 3; i++)
        {
            File.WriteAllText(Path.Combine(save, $"file{i}.rpgsave"), $"slot {i}");
        }

        // A folder with a save's name in the old profile's storage makes that copy fail.
        Directory.CreateDirectory(Path.Combine(workspace.ProfileSaves, "Default", "file2.rpgsave"));

        Assert.ThrowsAny<Exception>(() => engine.SwapSaveFiles("Default", "Second"));

        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal($"slot {i}", File.ReadAllText(Path.Combine(save, $"file{i}.rpgsave")));
        }
    }

    [Fact]
    public void SwapSaveFiles_ReplaceFailure_RestoresPreviousSaves()
    {
        (string game, ModEngine engine, GameWorkspacePaths workspace) = CreateGame();
        string save = Path.Combine(game, "save");
        Directory.CreateDirectory(save);
        File.WriteAllText(Path.Combine(save, "file1.rpgsave"), "old slot 1");
        File.WriteAllText(Path.Combine(save, "file2.rpgsave"), "old slot 2");
        string second = Path.Combine(workspace.ProfileSaves, "Second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "file1.rpgsave"), "new slot 1");
        File.WriteAllText(Path.Combine(second, "file9.rpgsave"), "new slot 9");

        // A folder in the live save folder with an incoming save's name makes the move fail.
        Directory.CreateDirectory(Path.Combine(save, "file9.rpgsave"));

        Assert.ThrowsAny<Exception>(() => engine.SwapSaveFiles("Default", "Second"));

        Assert.Equal("old slot 1", File.ReadAllText(Path.Combine(save, "file1.rpgsave")));
        Assert.Equal("old slot 2", File.ReadAllText(Path.Combine(save, "file2.rpgsave")));
    }

    [Fact]
    public void WindowsUpdateScript_AbortsWithoutBackupAndKeepsBackupWhenRestoreFails()
    {
        string script = UpdateService.BuildWindowsUpdateScript("RPGModder.exe", @"C:\Temp\RPGModder_Backup_test");
        string[] lines = script.Split("\r\n");

        int backupCopy = Array.FindIndex(lines, line => line.Contains("\"C:\\Temp\\RPGModder_Backup_test\\\" /exclude"));
        Assert.True(backupCopy > 0);
        Assert.Equal("if errorlevel 1 goto abort", lines[backupCopy + 1].Trim());

        int keepBackup = Array.FindIndex(lines, line => line.Trim() == ":keepbackup");
        Assert.True(keepBackup > 0);
        Assert.DoesNotContain(
            lines.Skip(keepBackup),
            line => line.Contains("rmdir") && line.Contains("RPGModder_Backup_test"));
    }

    [WindowsFact]
    public void WindowsUpdateScript_StagedUpdate_ReplacesInstallAndRemovesBackup()
    {
        (string app, string backup, string marker) = CreateWindowsInstall();

        RunWindowsUpdate(app, backup);

        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "version.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "lib", "inner.txt")));
        Assert.Equal("new", WaitForMarker(marker));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(Path.Combine(app, "_update_staging")));
    }

    [WindowsFact]
    public void WindowsUpdateScript_FailedUpdateCopy_RestoresPreviousVersion()
    {
        (string app, string backup, string marker) = CreateWindowsInstall();

        // The update ships a file where the install has a non-empty folder.
        // xcopy replaces the files before it, then fails on this one.
        Directory.CreateDirectory(Path.Combine(app, "zz_conflict"));
        File.WriteAllText(Path.Combine(app, "zz_conflict", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(app, "_update_staging", "zz_conflict"), "file");

        RunWindowsUpdate(app, backup);

        Assert.Equal("old", File.ReadAllText(Path.Combine(app, "version.txt")));
        Assert.Equal("old", WaitForMarker(marker));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(app, "zz_conflict", "keep.txt")));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void LinuxUpdateScript_UsesUnixLineEndingsAndKeepsBackupWhenRestoreFails()
    {
        string script = UpdateService.BuildLinuxUpdateScript("/opt/app", "/opt/app/_update_staging", "/tmp/backup", "/opt/app/RPGModder", "/tmp/update.sh", 0);

        Assert.DoesNotContain("\r", script);
        string tail = script[script.LastIndexOf("could not restore", StringComparison.Ordinal)..];
        Assert.DoesNotContain("rm -rf", tail);
        Assert.Contains("exit 1", tail);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void LinuxUpdateScript_FailedUpdateCopy_RestoresPreviousVersion()
    {
        string current = Path.Combine(_root, "app");
        string staging = Path.Combine(current, "_update_staging");
        string backup = Path.Combine(_root, "backup");
        string marker = Path.Combine(_root, "launched");
        Directory.CreateDirectory(staging);
        string executable = Path.Combine(current, "RPGModder");
        File.WriteAllText(executable, $"#!/bin/sh\necho old > '{marker}'\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(staging, "RPGModder"), $"#!/bin/sh\necho new > '{marker}'\n");

        // The update ships a file where the install has a non-empty folder, so cp fails midway.
        Directory.CreateDirectory(Path.Combine(current, "conflict"));
        File.WriteAllText(Path.Combine(current, "conflict", "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(staging, "conflict"), "file");

        string scriptPath = Path.Combine(_root, "update.sh");
        File.WriteAllText(scriptPath, UpdateService.BuildLinuxUpdateScript(current, staging, backup, executable, scriptPath, 0));
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { scriptPath } })!;
        process.WaitForExit(30_000);

        Assert.Equal("old", File.ReadAllText(marker).Trim());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(current, "conflict", "keep.txt")));
        Assert.False(Directory.Exists(backup));
    }

    private (string App, string Backup, string Marker) CreateWindowsInstall()
    {
        string app = Path.Combine(_root, "app");
        string staging = Path.Combine(app, "_update_staging");
        string marker = Path.Combine(_root, "launched");
        Directory.CreateDirectory(Path.Combine(app, "lib"));
        Directory.CreateDirectory(Path.Combine(staging, "lib"));
        File.WriteAllText(Path.Combine(app, "RPGModder.cmd"), RelaunchScript("old", marker));
        File.WriteAllText(Path.Combine(app, "version.txt"), "old");
        File.WriteAllText(Path.Combine(app, "lib", "inner.txt"), "old");
        File.WriteAllText(Path.Combine(staging, "RPGModder.cmd"), RelaunchScript("new", marker));
        File.WriteAllText(Path.Combine(staging, "version.txt"), "new");
        File.WriteAllText(Path.Combine(staging, "lib", "inner.txt"), "new");

        // A percent sign and an ampersand in the backup path prove that the script quotes and escapes it.
        return (app, Path.Combine(_root, "backup 100% & co"), marker);
    }

    // Stands in for the relaunched program and records which version started.
    // It is one line, so nothing inside the test folder is still open once the marker exists.
    private static string RelaunchScript(string version, string marker)
    {
        return $"@cd /d \"%SystemRoot%\" & echo {version}> \"{marker}\" & exit\r\n";
    }

    private static void RunWindowsUpdate(string app, string backup)
    {
        string script = UpdateService.WriteWindowsUpdateFiles(app, "RPGModder.cmd", backup);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/d", "/c", script },
            WorkingDirectory = app,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        })!;

        // A closed input makes the script's wait and pause return at once.
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(30_000), "The update script did not finish.");
    }

    private static string WaitForMarker(string marker)
    {
        string content = "";
        SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    content = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
                }
                catch (IOException)
                {
                    content = "";
                }

                return content.Length > 0;
            },
            TimeSpan.FromSeconds(15));
        return content;
    }

    private (string Game, ModEngine Engine, GameWorkspacePaths Workspace) CreateGame()
    {
        string game = Path.Combine(_root, "game");
        string data = Path.Combine(game, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "Actors.json"), "vanilla");
        string executable = Path.Combine(game, "Game.exe");
        File.WriteAllText(executable, "test");

        var engine = new ModEngine(executable);
        engine.InitializeSafeState();
        return (game, engine, new GameWorkspacePaths(game));
    }

    private static void WriteCopyMod(GameWorkspacePaths workspace, string id, string target, string content)
    {
        string mod = Path.Combine(workspace.Mods, id);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "payload.bin"), content);
        var manifest = new ModManifest
        {
            Metadata = new ModMetadata { Id = id, Name = id },
            FileOps = new List<FileOperation> { new() { Source = "payload.bin", Target = target } }
        };
        File.WriteAllText(Path.Combine(mod, "mod.json"), JsonConvert.SerializeObject(manifest));
    }
}

// Runs on Windows and reports as skipped elsewhere, so a pass always means the script was executed.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs the Windows update script, so it needs Windows.";
        }
    }
}

// Runs on Linux and reports as skipped elsewhere, so a pass always means the script was executed.
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs the POSIX update script, so it needs Linux.";
        }
    }
}
