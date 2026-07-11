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

    #region Game Detection

    private void LoadCachedGames()
    {
        UpdateCachedGamesCount();

        if (_settings.CachedGames.Count > 0)
        {
            foreach (var game in _settings.CachedGames)
            {
                DetectedGames.Add(game);
            }

            CmbDetectedGames.IsVisible = true;
            TxtGamePath.IsVisible = false;
        SetStatus($"Loaded {DetectedGames.Count} cached game(s). Select Scan to search again.", true);

            // Auto-select last used game
            if (!string.IsNullOrEmpty(_settings.Settings.LastGamePath))
            {
                var lastGame = DetectedGames.FirstOrDefault(g =>
                    g.ExePath.Equals(_settings.Settings.LastGamePath, PlatformService.PathComparison));
                if (lastGame != null)
                {
                    CmbDetectedGames.SelectedItem = lastGame;
                }
            }
        }
        else
        {
            SetStatus("Ready. Scan for games or browse to an executable.", true);
        }
    }

    private void StartGameScan()
    {
        try
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();

            SetStatus("Scanning for RPG Maker games...", true);
            BtnScanGames.IsEnabled = false;

            _ = _gameDetector.ScanForGamesAsync(_scanCts.Token);
        }
        catch (Exception ex)
        {
            SetStatus($"Scan failed: {ex.Message}", false);
            BtnScanGames.IsEnabled = true;
        }
    }

    private void BtnScanGames_Click(object? sender, RoutedEventArgs e)
    {
        DetectedGames.Clear();
        StartGameScan();
    }

    private void OnGameFound(DetectedGame game)
    {
        // Marshal to UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Check if already in list
            if (!DetectedGames.Any(g => g.ExePath.Equals(game.ExePath, PlatformService.PathComparison)))
            {
                DetectedGames.Add(game);
            }

            if (DetectedGames.Count == 1)
            {
                CmbDetectedGames.IsVisible = true;
                TxtGamePath.IsVisible = false;
            }

            SetStatus($"Found {DetectedGames.Count} game(s)...", true);
        });
    }

    private void OnScanComplete()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BtnScanGames.IsEnabled = true;

            // Save all detected games to cache
            _settings.SaveGames(DetectedGames);

            if (DetectedGames.Count > 0)
            {
                CmbDetectedGames.IsVisible = true;
                TxtGamePath.IsVisible = false;
                SetStatus($"Found {DetectedGames.Count} RPG Maker game(s). Select one to begin.", true);
            }
            else
            {
                CmbDetectedGames.IsVisible = false;
                TxtGamePath.IsVisible = true;
            SetStatus("No games found. Browse to a game executable manually.", true);
            }
        });
    }

    private void CmbDetectedGames_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbDetectedGames.SelectedItem is DetectedGame game)
        {
            _ = LoadGameAsync(game.ExePath, game.Name, game.Engine == RpgMakerEngine.MZ);
        }
    }

    #endregion

    
#region Game Selection & Initialization

    private void EvaluateModLoaderEnvironment()
    {
        if (string.IsNullOrEmpty(_gameRoot))
        {
            BadgeFhmm.IsVisible = false;
            BadgeTy.IsVisible = false;
            return;
        }

        bool isFhmm = false;
        bool isTy = false;

        // We check the deployed index.html to accurately reflect what is physically currently built
        string contentPath = _engine?.ContentPath ?? _gameRoot;
        string indexHtml = Path.Combine(contentPath, "index.html");

        if (File.Exists(indexHtml))
        {
            string content = File.ReadAllText(indexHtml);
            if (content.Contains("mattieFMModLoader.js", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("MATTIE", StringComparison.OrdinalIgnoreCase))
            {
                isFhmm = true;
            }
            if (content.Contains("TY_ModLoader", StringComparison.OrdinalIgnoreCase))
            {
                isTy = true;
            }
        }

        BadgeFhmm.IsVisible = isFhmm;
        BadgeTy.IsVisible = isTy;
    }

    private async void BtnFhmmHelp_Click(object? sender, RoutedEventArgs e)
    {
        await new RPGModder.UI.Dialogs.MessageBox(
            "FHMM Compatibility",
            "RPGModder safely manages Mattie's Fear & Hunger Mod Manager (FHMM) as a standard mod to protect your base game files.\n\n" +
            "DO NOT run the FHMM .exe installer directly on your game folder. Instead, ZIP the FHMM files and install them " +
            "through RPGModder like any other mod. We dynamically inject the mod loader when you click 'Apply Changes'."
        ).ShowDialog(this);
    }

    private async void BtnTyHelp_Click(object? sender, RoutedEventArgs e)
    {
        await new RPGModder.UI.Dialogs.MessageBox(
            "TY Mod Loader Compatibility",
            "RPGModder has safely deployed Toby Yasha's Mod Loader.\n\n" +
            "Just like FHMM, it is dynamically injected into your game when you click 'Apply Changes' and completely " +
            "removed to protect your game files when you disable it."
        ).ShowDialog(this);
    }

    private async void BtnSelect_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select RPG Maker Game Executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Game executable")
                {
                    Patterns = OperatingSystem.IsWindows() ? new[] { "*.exe" } : new[] { "Game", "game", "nw", "*.exe", "*" }
                }
            }
        });

        if (files.Count >= 1)
        {
            string path = files[0].Path.LocalPath;
            string dir = Path.GetDirectoryName(path)!;

            DetectedGame? detected = _gameDetector.DetectGameAt(dir);
            if (detected == null)
            {
                SetStatus("Error: Not a valid RPG Maker MV/MZ game.", false);
                return;
            }

            path = detected.ExePath;
            string gameName = detected.Name;

            // Add to detected games if not already there
            var existingGame = DetectedGames.FirstOrDefault(g =>
                g.ExePath.Equals(path, PlatformService.PathComparison));

            if (existingGame == null)
            {
                var newGame = new DetectedGame
                {
                    Name = gameName,
                    ExePath = path,
                    FolderPath = dir,
                    Engine = detected.Engine,
                    IconPath = detected.IconPath
                };
                DetectedGames.Add(newGame);
                _settings.AddGame(newGame);

                CmbDetectedGames.IsVisible = true;
                TxtGamePath.IsVisible = false;
                CmbDetectedGames.SelectedItem = newGame;
            }
            else
            {
                CmbDetectedGames.SelectedItem = existingGame;
            }
        }
    }

    private async System.Threading.Tasks.Task LoadGameAsync(string exePath, string gameName, bool isMZ)
    {
        try
        {
            // Clear previous state
            ClearGameState();

            string dir = Path.GetDirectoryName(exePath)!;

            _gameRoot = dir;
            _gameExePath = exePath;
            try
            {
                string testPath = Path.Combine(dir, ".rpgmodder_write_test");
                File.WriteAllText(testPath, "test");
                File.Delete(testPath);
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus(OperatingSystem.IsWindows()
                    ? "ERROR: No write permission. Run RPGModder as Administrator or move the game to a writable library."
                    : "ERROR: No write permission. Grant write access to the game folder or move it outside a read-only/sandboxed library.", false);
                ClearGameState();
                return;
            }
            TxtGamePath.Text = exePath;

            SetStatus("Initializing Safe State...", true);

            _gameSession = await _gameSessions.OpenAsync(exePath);
            _engine = _gameSession.Engine;
            _workspacePaths = _gameSession.Workspace;

            WorkspaceMigrationResult migration = _engine.MigrationResult;
            if (migration.ImportedProfiles > 0 || migration.ImportedMods > 0 || migration.Warnings.Count > 0)
            {
                string message = $"Recovered {migration.ImportedProfiles} profile(s) and {migration.ImportedMods} mod folder(s) from an earlier RPGModder version.";
                if (migration.Warnings.Count > 0)
                {
                    message += "\n\n" + string.Join("\n", migration.Warnings);
                }

                await new RPGModder.UI.Dialogs.MessageBox(
                    "Previous RPGModder data recovered",
                    message).ShowDialog(this);
            }

            if (_engine.JustCreatedSaveBackup)
            {
                await new RPGModder.UI.Dialogs.MessageBox(
                    "Save Backup Created",
                    "Since this is your first time using Profiles (or updating), we created a safety backup of your current save files.\n\n" +
                    "Location: .rpgmodder/backups/saves/original-vanilla\n\n" +
                    "If you ever lose progress, you can restore files from there."
                ).ShowDialog(this);
            }

            // --- FIX: READ ACTUAL ACTIVE PROFILE STATE ---
            string actualActiveProfile = GetActiveProfileMarker();
            _currentProfileName = actualActiveProfile;

            // Load the JSON data for the profile currently sitting in the live directory
            LoadProfileDataOnly(actualActiveProfile);

            RefreshProfileList(); // Updates the UI dropdown to match reality
            RefreshModList();
            RefreshDeploymentHistory();

            BtnInstallMod.IsEnabled = true;
            BtnLaunchGame.IsEnabled = true;
            BtnRebuild.IsEnabled = true;
            BtnOpenGameFolder.IsEnabled = true;
            BtnSaveManager.IsEnabled = true;

            // Save last used game
            _settings.Settings.LastGamePath = exePath;
            _settings.Save();

            SetStatus($"Loaded: {gameName} ({(isMZ ? "MZ" : "MV")})", true);

            EvaluateModLoaderEnvironment();

            // Detect and link Nexus game
            await DetectNexusGame();
            _ = RunBackgroundUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load game: {ex.Message}", false);
            ClearGameState();
        }
    }

    private void ClearGameState()
    {
        _engine = null;
        _profile = new ModProfile();
        _gameRoot = "";
        _gameExePath = "";
        _workspacePaths = null;
        _gameSession = null;
        _hasPendingChanges = false;

        // Cancel any in-flight Nexus operations
        _nexusCts?.Cancel();

        // Clear Nexus game state
        _currentNexusGame = null;
        _detectedGameName = "";
        UpdateNexusGameDisplay();
        NexusMods.Clear();
        ActiveConflicts.Clear();
        DeploymentHistory.Clear();
        TxtNexusSearch.IsEnabled = false;
        BtnNexusSearch.IsEnabled = false;
        BtnLinkNexusGame.IsEnabled = false;
        if (CmbNexusCategories != null) CmbNexusCategories.IsEnabled = false;
        if (CmbNexusSort != null) CmbNexusSort.IsEnabled = false;
        if (BtnNexusRefresh != null) BtnNexusRefresh.IsEnabled = false;

        // Clear mod lists
        foreach (var mod in _allMods)
            mod.PropertyChanged -= ModItem_PropertyChanged;
        _allMods.Clear();
        InstalledMods.Clear();

        // Clear search
        TxtSearch.Text = "";
        _searchFilter = "";

        PendingChangesIndicator.IsVisible = false;
        PlaceholderText.IsVisible = true;

        BtnInstallMod.IsEnabled = false;
        BtnLaunchGame.IsEnabled = false;
        BtnRebuild.IsEnabled = false;
        BtnOpenGameFolder.IsEnabled = false;

        _updateCheckCts?.Cancel();
        UpdateModCounts();
    }

    #endregion

    
}

