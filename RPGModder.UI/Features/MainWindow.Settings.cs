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
#region Settings

    private void UpdateCachedGamesCount()
    {
        TxtCachedGamesCount.Text = $"{_settings.CachedGames.Count} games cached";
    }

    private void BtnClearGamesCache_Click(object? sender, RoutedEventArgs e)
    {
        _settings.SaveGames(new List<DetectedGame>());
        DetectedGames.Clear();
        CmbDetectedGames.IsVisible = false;
        TxtGamePath.IsVisible = true;
        UpdateCachedGamesCount();
        SetStatus("Games cache cleared.", true);
    }

    private void BtnOpenAppData_Click(object? sender, RoutedEventArgs e)
    {
        string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RPGModder");

        if (!Directory.Exists(appDataPath))
            Directory.CreateDirectory(appDataPath);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appDataPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open folder: {ex.Message}", false);
        }
    }

    private async void BtnResetBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_gameRoot))
        {
            SetStatus("No game loaded.", false);
            return;
        }

        string backupPath = RequireWorkspace().CleanBackup;
        if (!Directory.Exists(backupPath))
        {
            SetStatus("No backup exists to reset.", false);
            return;
        }

        try
        {
            var confirmation = new RPGModder.UI.Dialogs.MessageBox(
                "Recreate vanilla snapshot",
                "RPGModder will first restore the existing vanilla snapshot, then create a new one. Profiles and save backups are preserved. Continue?",
                true);
            if (!await confirmation.ShowDialog<bool>(this))
            {
                return;
            }

            SetStatus("Resetting vanilla backup...", true);
            if (_engine != null)
            {
                OperationResult restoreResult = await System.Threading.Tasks.Task.Run(() =>
                    _engine.RebuildGame(new ModProfile()));
                if (!restoreResult.Success)
                {
                    SetStatus("Could not restore vanilla state. The existing snapshot was preserved.", false);
                    return;
                }
            }

            await System.Threading.Tasks.Task.Run(() => Directory.Delete(backupPath, true));

            // Re-initialize
            if (_engine != null)
            {
                _gameSession = await _gameSessions.OpenAsync(_gameExePath);
                _engine = _gameSession.Engine;
                _workspacePaths = _gameSession.Workspace;
            }

            SetStatus("Vanilla backup reset. A fresh backup will be created.", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to reset backup: {ex.Message}", false);
        }
    }

    #endregion

    
}

