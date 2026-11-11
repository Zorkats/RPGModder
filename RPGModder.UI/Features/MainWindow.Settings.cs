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
        if (_gameSession == null)
        {
            SetStatus("No game loaded.", false);
            return;
        }

        ConfirmChoice choice = await new ConfirmDialog(
            "Recreate vanilla snapshot",
            "RPGModder will first restore the existing vanilla snapshot, then create a new one from the restored game folder. Profiles and save backups are kept.",
            "Recreate").AskAsync(this);
        if (choice != ConfirmChoice.Confirm)
        {
            return;
        }

        SetStatus("Resetting vanilla backup...", true);
        OperationResult result = await _vanillaBackup.ResetAsync(_gameSession);
        await RefreshPendingChangesAsync();
        if (!result.Success)
        {
            SetStatus($"Failed to reset backup: {FirstErrorMessage(result)}", false);
            return;
        }

        SetStatus("Vanilla backup reset. The game folder holds vanilla until the next deploy.", true);
    }

    #endregion

    
}

