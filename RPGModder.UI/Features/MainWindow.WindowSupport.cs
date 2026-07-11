using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using RPGModder.Core.Application;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using RPGModder.UI.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace RPGModder.UI;

public partial class MainWindow
{
#region Save Manager

    private async void BtnSaveManager_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine == null || string.IsNullOrEmpty(_gameRoot)) return;

        // Pass game root and "usesWww" flag to locate saves correctly
        var win = new RPGModder.UI.Dialogs.SaveManagerWindow(_gameRoot, _engine.UsesWwwFolder);
        await win.ShowDialog(this);
    }

    #endregion

#region Helpers

    private void SetStatus(string message, bool success)
    {
        TxtStatus.Text = message;
        StatusIndicator.Background = GetStatusBrush(success);
    }

    private IBrush GetStatusBrush(bool success)
    {
        string key = success ? "SuccessBrush" : "DangerBrush";
        if (Application.Current?.Resources.TryGetResource(key, null, out var brush) == true)
            return (IBrush)brush!;
        return success ? Brushes.Green : Brushes.Red;
    }

    private GameWorkspacePaths RequireWorkspace()
    {
        return _workspacePaths ?? throw new InvalidOperationException("Load a game before accessing its workspace.");
    }

    private void RefreshConflictWorkspace()
    {
        ConflictReport report = _conflictDetector.GenerateReport(_allMods);
        ActiveConflicts.Clear();
        foreach (FileConflict conflict in report.Conflicts)
        {
            ActiveConflicts.Add(conflict);
        }

        TxtConflictSummary.Text = ActiveConflicts.Count == 0
            ? "No conflicts among enabled mods"
            : $"{ActiveConflicts.Count} conflicted file(s) among enabled mods";
    }

    private void RefreshDeploymentHistory()
    {
        if (_workspacePaths == null)
        {
            return;
        }

        DeploymentHistory.Clear();
        foreach (DeploymentHistoryEntry entry in _deploymentHistory.Load(_workspacePaths))
        {
            DeploymentHistory.Add(entry);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Cancel any in-flight operations
        _scanCts?.Cancel();
        _nexusCts?.Cancel();

        // Cleanup temp files
        CleanupTempExtract();

        // Dispose services that hold unmanaged resources
        _nexus.Dispose();
        _downloadManager.Dispose();
        _updater.Dispose();
    }

    private async void CheckUpdatesOnStartup()
    {
        // Don't block the UI thread, wait a few seconds so the app loads first
        await Task.Delay(2000);

        try
        {
            var update = await _updater.CheckForUpdatesAsync();
            if (update != null)
            {
                var dialog = new RPGModder.UI.Dialogs.UpdateDialog(_updater, update);
                await dialog.ShowDialog(this);
            }
        }
        catch { } // Fail silently on startup checks
    }

    #endregion

}

