using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
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

public partial class MainWindow : Window
{
    private ModEngine? _engine;
    private ModProfile _profile = new();
    private string _currentProfileName = "Default";
    private string _gameRoot = "";
    private string _gameExePath = "";
    private bool _hasPendingChanges = false;

    // Services
    private readonly ModPackerService _packer = new();
    private readonly ModInstallerService _installer = new();
    private readonly GameDetectorService _gameDetector = new();
    private readonly SettingsService _settings = new();
    private readonly ConflictDetectionService _conflictDetector = new();
    private readonly NexusApiService _nexus = new();
    private readonly SteamLaunchService _steamLauncher = new();
    private readonly UpdateService _updater = new();
    private readonly DownloadManager _downloadManager = new();
    private PackerResult? _currentAnalysis;
    private string? _tempExtractPath;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _nexusCts; // Cancels in-flight Nexus API calls
    
    // Nexus game linking
    private NexusGameMapping? _currentNexusGame;
    private string _detectedGameName = "";

    // UI Bindings
    public ObservableCollection<ModListItem> InstalledMods { get; set; } = new();
    public ObservableCollection<ChangeItem> DetectedChanges { get; set; } = new();
    public ObservableCollection<DetectedGame> DetectedGames { get; set; } = new();
    public ObservableCollection<NexusMod> NexusMods { get; set; } = new();
    public ObservableCollection<NexusGame> NexusGameResults { get; set; } = new();

    public MainWindow()
    {
        InitializeComponent();

        // 1. Get the version dynamically
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        string versionString = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";

        // 2. Update BOTH labels
        TxtVersionFooter.Text = versionString;
        TxtVersionAbout.Text = $"Version {versionString.TrimStart('v')}";

        LstMods.ItemsSource = InstalledMods;
        LstChanges.ItemsSource = DetectedChanges;
        CmbDetectedGames.ItemsSource = DetectedGames;

        // My Mods tab events
        BtnSelect.Click += BtnSelect_Click;
        BtnScanGames.Click += BtnScanGames_Click;
        CmbDetectedGames.SelectionChanged += CmbDetectedGames_SelectionChanged;
        MenuInstallZip.Click += MenuInstallZip_Click;
        MenuInstallFolder.Click += MenuInstallFolder_Click;
        BtnLaunchGame.Click += BtnLaunchGame_Click;
        BtnRebuild.Click += BtnRebuild_Click;
        BtnOpenGameFolder.Click += BtnOpenGameFolder_Click;
        DropZone.AddHandler(DragDrop.DropEvent, DropZone_Drop);
        DropZone.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);

        // Mod list drag-drop reordering
        LstMods.AddHandler(DragDrop.DragOverEvent, LstMods_DragOver);
        LstMods.AddHandler(DragDrop.DropEvent, LstMods_Drop);
        LstMods.PointerPressed += DragHandle_PointerPressed;

        // Creator Tools tab events
        BtnBrowseWork.Click += BtnBrowseWork_Click;
        BtnBrowseWorkZip.Click += BtnBrowseWorkZip_Click;
        BtnBrowseVanilla.Click += BtnBrowseVanilla_Click;
        BtnAnalyze.Click += BtnAnalyze_Click;
        BtnGeneratePackage.Click += BtnGeneratePackage_Click;

        // Phase 1.2: Search
        TxtSearch.TextChanged += TxtSearch_TextChanged;
        BtnClearSearch.Click += BtnClearSearch_Click;

        // Phase 1.2: Context menu
        CtxOpenFolder.Click += CtxOpenFolder_Click;
        CtxViewManifest.Click += CtxViewManifest_Click;
        CtxViewConflicts.Click += CtxViewConflicts_Click;
        CtxEnable.Click += CtxEnable_Click;
        CtxDisable.Click += CtxDisable_Click;
        CtxRemove.Click += CtxRemove_Click;

        // Phase 1.3: Load order
        CtxMoveUp.Click += CtxMoveUp_Click;
        CtxMoveDown.Click += CtxMoveDown_Click;

        // Phase 1.2: Settings
        BtnClearGamesCache.Click += BtnClearGamesCache_Click;
        BtnOpenAppData.Click += BtnOpenAppData_Click;
        BtnResetBackup.Click += BtnResetBackup_Click;

        // Phase 2: Nexus Mods
        BtnNexusConnect.Click += BtnNexusConnect_Click;
        BtnNexusSearch.Click += BtnNexusSearch_Click;
        TxtNexusSearch.KeyDown += TxtNexusSearch_KeyDown;
        BtnSaveNexusKey.Click += BtnSaveNexusKey_Click;
        BtnRegisterNxm.Click += BtnRegisterNxm_Click;
        BtnLinkNexusGame.Click += BtnLinkNexusGame_Click;
        BtnSearchNexusGame.Click += BtnSearchNexusGame_Click;
        BtnCancelLinkGame.Click += BtnCancelLinkGame_Click;
        TxtNexusGameSearch.KeyDown += TxtNexusGameSearch_KeyDown;
        BtnNexusAll.Click += BtnNexusAll_Click;
        BtnNexusLatest.Click += BtnNexusLatest_Click;
        BtnNexusTrending.Click += BtnNexusTrending_Click;
        BtnNexusUpdated.Click += BtnNexusUpdated_Click;
        BtnNexusRefresh.Click += BtnNexusRefresh_Click;
        LstNexusMods.ItemsSource = NexusMods;

        // Game detector events
        _gameDetector.GameFound += OnGameFound;
        _gameDetector.ScanComplete += OnScanComplete;
        
        // NXM protocol IPC - receive URLs from other instances
        Program.NxmUrlReceived += OnNxmUrlReceived;
        
        // Handle initial NXM URL passed via command line
        Opened += async (s, e) =>
        {
            if (!string.IsNullOrEmpty(Program.InitialNxmUrl))
            {
                await Task.Delay(500); // Let the window finish loading
                await HandleNxmUrl(Program.InitialNxmUrl);
            }
        };

        // Load cached games
        LoadCachedGames();

        // Initialize Mod Profiles
        InitializeProfiles();

        // Initialize Nexus state
        InitializeNexusState();

        // Initialize Auto-Updater
        CheckUpdatesOnStartup();
    }
    
    private async void OnNxmUrlReceived(string url)
    {
        // Dispatch to UI thread
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Bring window to front
            Activate();
            Topmost = true;
            Topmost = false;
            
            await HandleNxmUrl(url);
        });
    }
    
    private async Task HandleNxmUrl(string url)
    {
        if (!url.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            return;

        SetStatus($"Processing NXM download: {url}", true);

        // Parse the NXM URL
        // Format: nxm://gameDomain/mods/modId/files/fileId?key=xxx&expires=xxx&user_id=xxx
        try
        {
            var uri = new Uri(url);
            var gameDomain = uri.Host;
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            if (segments.Length >= 4 && segments[0] == "mods" && segments[2] == "files")
            {
                var modId = int.Parse(segments[1]);
                var fileId = int.Parse(segments[3]);

                // Parse query params
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var key = query["key"] ?? "";
                var expires = long.Parse(query["expires"] ?? "0");
                var userId = int.Parse(query["user_id"] ?? "0");

                if (!_nexus.IsAuthenticated)
                {
                    SetStatus("Connect to Nexus first to download mods", false);
                    MainTabControl.SelectedIndex = 2; // Nexus Mods tab
                    return;
                }

                // Get download links
                var links = await _nexus.GetDownloadLinksFromNxmAsync(gameDomain, modId, fileId, key, expires, userId);

                if (links.Count > 0)
                {
                    // Get mod info (Required for the installer to know the ID/Version)
                    var modInfo = await _nexus.GetModAsync(gameDomain, modId);

                    if (modInfo != null)
                    {
                        // Pass filename with .zip hint — DownloadManager will resolve the
                        // real name from Content-Disposition if available
                        string fileHint = $"{modInfo.Name}.zip";
                        await DownloadAndInstallMod(links[0].Uri, fileHint, modInfo);
                    }
                    else
                    {
                        SetStatus("Could not retrieve mod details from Nexus.", false);
                    }
                }
                else
                {
                    SetStatus("Could not get download link - you may need Nexus Premium.", false);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to process NXM URL: {ex.Message}", false);
        }
    }

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
            SetStatus($"Loaded {DetectedGames.Count} cached game(s). Click 🔍 to scan for more.", true);
            
            // Auto-select last used game
            if (!string.IsNullOrEmpty(_settings.Settings.LastGamePath))
            {
                var lastGame = DetectedGames.FirstOrDefault(g => 
                    g.ExePath.Equals(_settings.Settings.LastGamePath, StringComparison.OrdinalIgnoreCase));
                if (lastGame != null)
                {
                    CmbDetectedGames.SelectedItem = lastGame;
                }
            }
        }
        else
        {
            SetStatus("Ready. Click 🔍 to scan for games or 📁 to browse manually.", true);
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
            if (!DetectedGames.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
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
                SetStatus("No games found. Use 📁 to browse manually.", true);
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

    private async void BtnSelect_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Game Executable",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Executable") { Patterns = new[] { "*.exe" } } }
        });

        if (files.Count >= 1)
        {
            string path = files[0].Path.LocalPath;
            string dir = Path.GetDirectoryName(path)!;

            bool isMV = File.Exists(Path.Combine(dir, "js", "rpg_core.js"));
            bool isMZ = File.Exists(Path.Combine(dir, "js", "rmmz_core.js"));

            if (!isMV && !isMZ)
            {
                SetStatus("Error: Not a valid RPG Maker MV/MZ game.", false);
                return;
            }

            string gameName = Path.GetFileNameWithoutExtension(path);
            
            // Add to detected games if not already there
            var existingGame = DetectedGames.FirstOrDefault(g => 
                g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            
            if (existingGame == null)
            {
                var newGame = new DetectedGame
                {
                    Name = gameName,
                    ExePath = path,
                    FolderPath = dir,
                    Engine = isMZ ? RpgMakerEngine.MZ : RpgMakerEngine.MV
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
                SetStatus("ERROR: No write permission! Please run RPGModder as Administrator.", false);
                ClearGameState();
                return;
            }
            TxtGamePath.Text = exePath;
            TxtGameName.Text = gameName;
            GameBadge.IsVisible = true;

            SetStatus("Initializing Safe State...", true);
            
            _engine = new ModEngine(exePath);
            await System.Threading.Tasks.Task.Run(() => _engine.InitializeSafeState());

            if (_engine.JustCreatedSaveBackup)
            {
                await new RPGModder.UI.Dialogs.MessageBox(
                    "Save Backup Created",
                    "Since this is your first time using Profiles (or updating), we created a safety backup of your current save files.\n\n" +
                    "Location: ModManager_Backups/Saves/ORIGINAL_VANILLA\n\n" +
                    "If you ever lose progress, you can restore files from there."
                ).ShowDialog(this);
            }

            _profile = new ModProfile();
            LoadProfile();
            RefreshModList();

            BtnInstallMod.IsEnabled = true;
            BtnLaunchGame.IsEnabled = true;
            BtnRebuild.IsEnabled = true;
            BtnOpenGameFolder.IsEnabled = true;
            BtnSaveManager.IsEnabled = true;

            // Save last used game
            _settings.Settings.LastGamePath = exePath;
            _settings.Save();

            SetStatus($"Loaded: {gameName} ({(isMZ ? "MZ" : "MV")})", true);
            
            // Detect and link Nexus game
            await DetectNexusGame();
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
        _hasPendingChanges = false;
        
        // Cancel any in-flight Nexus operations
        _nexusCts?.Cancel();
        
        // Clear Nexus game state
        _currentNexusGame = null;
        _detectedGameName = "";
        UpdateNexusGameDisplay();
        NexusMods.Clear();
        TxtNexusSearch.IsEnabled = false;
        BtnNexusSearch.IsEnabled = false;
        BtnLinkNexusGame.IsEnabled = false;
        BtnNexusAll.IsEnabled = false;
        BtnNexusLatest.IsEnabled = false;
        BtnNexusTrending.IsEnabled = false;
        BtnNexusUpdated.IsEnabled = false;
        BtnNexusRefresh.IsEnabled = false;
        
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
        
        UpdateModCounts();
    }

    #endregion

    #region Mod Installation

    private async void MenuInstallZip_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Mod ZIP File(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ZIP Archives") { Patterns = new[] { "*.zip" } }
            }
        });

        foreach (var file in files)
            await InstallModFromPath(file.Path.LocalPath);
    }

    private async void MenuInstallFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Mod Folder(s)",
            AllowMultiple = true
        });

        foreach (var folder in folders)
            await InstallModFromPath(folder.Path.LocalPath);
    }

    private void BtnOpenGameFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_gameRoot) || !Directory.Exists(_gameRoot))
        {
            SetStatus("No game folder loaded.", false);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _gameRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open folder: {ex.Message}", false);
        }
    }

    private async System.Threading.Tasks.Task InstallModFromPath(string path)
    {
        if (_engine == null) return;

        string modsDir = Path.Combine(_gameRoot, "Mods");
        Directory.CreateDirectory(modsDir);

        SetStatus($"Installing: {Path.GetFileName(path)}...", true);

        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
                _installer.InstallMod(path, modsDir));

            if (result.Success && result.FolderName != null)
            {
                if (!_profile.EnabledMods.Contains(result.FolderName))
                {
                    _profile.EnabledMods.Add(result.FolderName);
                    SaveProfile();
                }

                RefreshModList();
                MarkPendingChanges();
                SetStatus($"✓ Installed: {result.Manifest?.Metadata.Name ?? result.FolderName} — Click 'Apply Changes' to activate", true);
            }
            else
            {
                SetStatus($"Install failed: {result.Error}", false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Install error: {ex.Message}", false);
        }
    }

    #endregion

    #region Mod List Management

    private void RefreshModList()
    {
        // Clear existing subscriptions
        foreach (var mod in _allMods)
        {
            mod.PropertyChanged -= ModItem_PropertyChanged;
        }
        _allMods.Clear();
        InstalledMods.Clear();
        
        string modsRoot = Path.Combine(_gameRoot, "Mods");
        if (!Directory.Exists(modsRoot))
        {
            PlaceholderText.IsVisible = true;
            return;
        }

        var loadedMods = new List<ModListItem>();

        foreach (var dir in Directory.GetDirectories(modsRoot))
        {
            string jsonPath = Path.Combine(dir, "mod.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(jsonPath));
                    if (manifest != null)
                    {
                        string folderName = new DirectoryInfo(dir).Name;
                        bool isEnabled = _profile.EnabledMods.Contains(folderName);

                        var item = new ModListItem(manifest, folderName, isEnabled);
                        item.PropertyChanged += ModItem_PropertyChanged;
                        loadedMods.Add(item);
                    }
                }
                catch { }
            }
        }

        // Sort by load order from profile, new mods go to the end
        var orderedMods = new List<ModListItem>();
        
        // First add mods in profile order
        foreach (var folderName in _profile.LoadOrder)
        {
            var mod = loadedMods.FirstOrDefault(m => m.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (mod != null)
            {
                orderedMods.Add(mod);
                loadedMods.Remove(mod);
            }
        }
        
        // Add any remaining mods (new installs) at the end
        orderedMods.AddRange(loadedMods);

        // Assign load order indices
        for (int i = 0; i < orderedMods.Count; i++)
        {
            orderedMods[i].LoadOrder = i;
        }

        _allMods.AddRange(orderedMods);

        // Detect conflicts
        _conflictDetector.DetectConflicts(_allMods);

        // Apply current search filter
        ApplySearchFilter();
    }

    private void ModItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModListItem.IsEnabled))
        {
            MarkPendingChanges();
            UpdateModCounts();
        }
    }

    private void UpdateModCounts()
    {
        int total = _allMods.Count;
        int active = _allMods.Count(m => m.IsEnabled);
        TxtTotalMods.Text = total.ToString();
        TxtActiveMods.Text = active.ToString();
    }

    private void MarkPendingChanges()
    {
        _hasPendingChanges = true;
        PendingChangesIndicator.IsVisible = true;
        SetStatus("You have unsaved changes. Click 'Apply Changes' to rebuild.", true);
    }

    private void ClearPendingChanges()
    {
        _hasPendingChanges = false;
        PendingChangesIndicator.IsVisible = false;
    }

    #endregion

    #region Mod Actions

    private async void BtnRebuild_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine == null) return;

        string gameProcessName = Path.GetFileNameWithoutExtension(_gameExePath);
        if (Process.GetProcessesByName(gameProcessName).Any())
        {
            SetStatus("⚠️ Cannot rebuild: The game is running! Please close it first.", false);
            return;
        }

        SetStatus("Rebuilding game...", true);
        BtnRebuild.IsEnabled = false;

        try
        {
            _profile.EnabledMods.Clear();
            foreach (var mod in _allMods.Where(m => m.IsEnabled))
                _profile.EnabledMods.Add(mod.FolderName);
            SaveProfile();

            // Apply smart merge setting, hardcore, and symlinks.
            _engine.UseMerging = ChkSmartMerge.IsChecked ?? true;
            _engine.UseHardcoreMerging = ChkHardcoreMerge.IsChecked ?? false;
            _engine.UseSymlinks = ChkUseSymlinks.IsChecked ?? false;

            await System.Threading.Tasks.Task.Run(() => _engine.RebuildGame(_profile));

            // Build status message
            string statusMsg = $"Rebuild complete! {_profile.EnabledMods.Count} mod(s) active.";
            
            // Report merging results if enabled
            if (_engine.UseMerging && _engine.LastMergeReports.Count > 0)
            {
                int totalMerged = _engine.LastMergeReports.Sum(r => r.MergedRecords);
                int totalConflicts = _engine.LastMergeReports.Sum(r => r.Conflicts.Count);
                
                if (totalMerged > 0 || totalConflicts > 0)
                {
                    statusMsg += $" (Merged: {totalMerged} records";
                    if (totalConflicts > 0)
                        statusMsg += $", {totalConflicts} conflicts resolved";
                    statusMsg += ")";
                }
            }

            ClearPendingChanges();
            SetStatus(statusMsg, true);
        }
        catch (Exception ex)
        {
            SetStatus($"Rebuild failed: {ex.Message}", false);
        }
        finally
        {
            BtnRebuild.IsEnabled = true;
        }
    }

    private void BtnLaunchGame_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_gameExePath) || !File.Exists(_gameExePath))
        {
            SetStatus("Error: Game executable not found.", false);
            return;
        }

        if (_hasPendingChanges)
        {
            SetStatus("Cannot launch: You have pending changes! Click 'Apply Changes' (Rebuild) first.", false);
            return;
        }

        try
        {
            var launchMethod = _steamLauncher.GetLaunchMethodName(_gameRoot, _detectedGameName);
            var success = _steamLauncher.LaunchGame(_gameExePath, _gameRoot, _detectedGameName, preferSteam: true);
            
            if (success)
            {
                SetStatus($"Game launched via {launchMethod}!", true);
            }
            else
            {
                SetStatus("Failed to launch game.", false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch: {ex.Message}", false);
        }
    }

    private async void BtnRemoveMod_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string folderName) return;

        var modItem = _allMods.FirstOrDefault(m => m.FolderName == folderName);
        if (modItem == null) return;

        await RemoveModAsync(modItem);
    }

    #endregion

    #region Drag & Drop

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        if (_engine != null && e.Data.Contains(DataFormats.Files))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (_engine == null) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var item in files)
        {
            string path = item.Path.LocalPath;
            if (Directory.Exists(path) || (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                await InstallModFromPath(path);
            }
        }
    }

    #endregion

    #region Creator Tools - Auto-Packer

    private async void BtnBrowseWork_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolder("Select Your Modded Game Folder");
        if (folder != null)
        {
            CleanupTempExtract();
            TxtWorkFolder.Text = folder;
            ClearAnalysis();
        }
    }

    private async void BtnBrowseWorkZip_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Modded Game ZIP",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ZIP Files") { Patterns = new[] { "*.zip" } } }
        });

        if (files.Count >= 1)
        {
            string zipPath = files[0].Path.LocalPath;

            TxtPackerStatus.Text = "Extracting ZIP...";
            CleanupTempExtract();

            try
            {
                _tempExtractPath = Path.Combine(Path.GetTempPath(), $"RPGModder_Work_{Guid.NewGuid():N}");
                await System.Threading.Tasks.Task.Run(() =>
                {
                    Directory.CreateDirectory(_tempExtractPath);
                    ZipFile.ExtractToDirectory(zipPath, _tempExtractPath);
                });

                TxtWorkFolder.Text = $"[ZIP] {Path.GetFileName(zipPath)}";
                ClearAnalysis();
                TxtPackerStatus.Text = "ZIP extracted. Ready to analyze.";
            }
            catch (Exception ex)
            {
                TxtPackerStatus.Text = $"Failed to extract ZIP: {ex.Message}";
                CleanupTempExtract();
            }
        }
    }

    private async void BtnBrowseVanilla_Click(object? sender, RoutedEventArgs e)
    {
        // 1. Auto-Detect Feature: Check for our own Safe-State Backup
        if (!string.IsNullOrEmpty(_gameRoot))
        {
            string cleanBackup = Path.Combine(_gameRoot, "ModManager_Backups", "Clean_Vanilla");
            if (Directory.Exists(cleanBackup))
            {
                if (string.IsNullOrEmpty(TxtVanillaFolder.Text))
                {
                    TxtVanillaFolder.Text = cleanBackup;
                    SetStatus("Auto-selected clean vanilla backup from current game.", true);
                    ClearAnalysis();
                    return;
                }
            }
        }

        // 2. Manual Fallback
        var folder = await PickFolder("Select Clean Vanilla Game Folder");
        if (folder != null)
        {
            TxtVanillaFolder.Text = folder;
            ClearAnalysis();
        }
    }

    private void CtxRemoveChange_Click(object? sender, RoutedEventArgs e)
    {
        if (LstChanges.SelectedItem is not ChangeItem item || _currentAnalysis == null) return;

        // 1. Remove from UI
        DetectedChanges.Remove(item);

        // 2. Remove from Analysis Data (So it doesn't get generated into mod.json)
        string path = item.Path;

        switch (item.Type)
        {
            case "NEW":
                if (_currentAnalysis.NewFiles.ContainsKey(path))
                    _currentAnalysis.NewFiles.Remove(path);
                break;

            case "MOD":
                if (_currentAnalysis.ModifiedFiles.ContainsKey(path))
                    _currentAnalysis.ModifiedFiles.Remove(path);
                break;

            case "PATCH":
                if (_currentAnalysis.JsonPatches.ContainsKey(path))
                    _currentAnalysis.JsonPatches.Remove(path);
                break;

            case "PLUGIN":
                var plugin = _currentAnalysis.NewPlugins.FirstOrDefault(p => p.Name == path);
                if (plugin != null) _currentAnalysis.NewPlugins.Remove(plugin);
                break;
        }

        // Update counts

        TxtChangeCount.Text = $"{_currentAnalysis.TotalChanges} changes";

        if (_currentAnalysis.TotalChanges == 0)
        {
            BtnGeneratePackage.IsEnabled = false;
            TxtPackerStatus.Text = "All changes removed.";
        }
    }

    private async System.Threading.Tasks.Task<string?> PickFolder(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count >= 1 ? folders[0].Path.LocalPath : null;
    }

    private async void BtnAnalyze_Click(object? sender, RoutedEventArgs e)
    {
        string workFolder = _tempExtractPath ?? TxtWorkFolder.Text ?? "";
        string vanillaFolder = TxtVanillaFolder.Text ?? "";

        if (string.IsNullOrWhiteSpace(workFolder) || string.IsNullOrWhiteSpace(vanillaFolder))
        {
            TxtPackerStatus.Text = "Please select both folders first.";
            return;
        }

        TxtPackerStatus.Text = "Analyzing...";
        BtnAnalyze.IsEnabled = false;

        try
        {
            _currentAnalysis = await System.Threading.Tasks.Task.Run(() =>
                _packer.AnalyzeDifferences(workFolder, vanillaFolder));

            if (!_currentAnalysis.Success)
            {
                TxtPackerStatus.Text = _currentAnalysis.ErrorMessage ?? "Analysis failed.";
                ClearAnalysis();
                return;
            }

            DetectedChanges.Clear();

            foreach (var (path, _) in _currentAnalysis.NewFiles)
                DetectedChanges.Add(new ChangeItem("NEW", path, "#4CAF50"));

            foreach (var (path, _) in _currentAnalysis.ModifiedFiles)
                DetectedChanges.Add(new ChangeItem("MOD", path, "#FF9800"));

            foreach (var (path, _) in _currentAnalysis.JsonPatches)
                DetectedChanges.Add(new ChangeItem("PATCH", path, "#2196F3"));

            foreach (var plugin in _currentAnalysis.NewPlugins)
                DetectedChanges.Add(new ChangeItem("PLUGIN", plugin.Name, "#9C27B0"));

            foreach (var warning in _currentAnalysis.Warnings)
                DetectedChanges.Add(new ChangeItem("WARN", warning, "#FFC107"));

            TxtChangeCount.Text = $"{_currentAnalysis.TotalChanges} changes";
            MetadataPanel.IsVisible = _currentAnalysis.TotalChanges > 0;
            BtnGeneratePackage.IsEnabled = _currentAnalysis.TotalChanges > 0;

            TxtPackerStatus.Text = _currentAnalysis.TotalChanges == 0
                ? "No differences found."
                : $"Found {_currentAnalysis.TotalChanges} changes.";
        }
        catch (Exception ex)
        {
            TxtPackerStatus.Text = $"Error: {ex.Message}";
            ClearAnalysis();
        }
        finally
        {
            BtnAnalyze.IsEnabled = true;
        }
    }

    private async void BtnGeneratePackage_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentAnalysis == null || _currentAnalysis.TotalChanges == 0)
        {
            TxtPackerStatus.Text = "Please analyze folders first.";
            return;
        }

        string modName = TxtModName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modName))
        {
            TxtPackerStatus.Text = "Please enter a mod name.";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder"
        });

        if (folders.Count == 0) return;

        string outputRoot = folders[0].Path.LocalPath;
        string safeFolderName = string.Join("_", modName.Split(Path.GetInvalidFileNameChars()));
        string outputFolder = Path.Combine(outputRoot, safeFolderName);

        try
        {
            TxtPackerStatus.Text = "Generating package...";
            BtnGeneratePackage.IsEnabled = false;

            var metadata = new ModMetadata
            {
                Name = modName,
                Author = TxtModAuthor.Text?.Trim() ?? "Unknown",
                Version = TxtModVersion.Text?.Trim() ?? "1.0",
                Description = TxtModDescription.Text?.Trim() ?? "",
                Id = safeFolderName.ToLowerInvariant()
            };

            var manifest = _packer.GenerateManifest(_currentAnalysis, metadata);

            await System.Threading.Tasks.Task.Run(() =>
                _packer.CreateModPackage(outputFolder, manifest, _currentAnalysis));

            TxtPackerStatus.Text = $"✅ Package created!";

            try { Process.Start(new ProcessStartInfo { FileName = outputFolder, UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex)
        {
            TxtPackerStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnGeneratePackage.IsEnabled = true;
        }
    }

    private void ClearAnalysis()
    {
        _currentAnalysis = null;
        DetectedChanges.Clear();
        TxtChangeCount.Text = "";
        MetadataPanel.IsVisible = false;
        BtnGeneratePackage.IsEnabled = false;
    }

    private void CleanupTempExtract()
    {
        if (_tempExtractPath != null && Directory.Exists(_tempExtractPath))
        {
            try { Directory.Delete(_tempExtractPath, true); }
            catch { }
            _tempExtractPath = null;
        }
    }

    #endregion

    #region Profile Management

    private void LoadProfile()
    {
        string profilePath = Path.Combine(_gameRoot, "profile.json");
        if (File.Exists(profilePath))
        {
            try
            {
                var data = JsonConvert.DeserializeObject<ModProfile>(File.ReadAllText(profilePath));
                if (data != null) _profile = data;
            }
            catch { _profile = new ModProfile(); }
        }
    }

    private void SaveProfile()
    {
        // Update load order from current _allMods list
        _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();
        
        string profilePath = Path.Combine(_gameRoot, "profile.json");
        File.WriteAllText(profilePath, JsonConvert.SerializeObject(_profile, Formatting.Indented));
    }

    #endregion

    #region  Search & Filter

    private string _searchFilter = "";
    private List<ModListItem> _allMods = new();

    private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchFilter = TxtSearch.Text?.Trim() ?? "";
        BtnClearSearch.IsVisible = !string.IsNullOrEmpty(_searchFilter);
        ApplySearchFilter();
    }

    private void BtnClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        TxtSearch.Text = "";
    }

    private void ApplySearchFilter()
    {
        InstalledMods.Clear();

        var filtered = string.IsNullOrEmpty(_searchFilter)
            ? _allMods
            : _allMods.Where(m =>
                m.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                m.Description.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase));

        foreach (var mod in filtered)
        {
            InstalledMods.Add(mod);
        }

        UpdateModCounts();
        PlaceholderText.IsVisible = InstalledMods.Count == 0 && _allMods.Count == 0;
    }

    #endregion

    #region Context Menu

    private ModListItem? GetSelectedMod()
    {
        return LstMods.SelectedItem as ModListItem;
    }

    private void CtxOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null || string.IsNullOrEmpty(_gameRoot)) return;

        string modPath = Path.Combine(_gameRoot, "Mods", mod.FolderName);
        if (Directory.Exists(modPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = modPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open folder: {ex.Message}", false);
            }
        }
    }

    private void CtxViewManifest_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null || string.IsNullOrEmpty(_gameRoot)) return;

        string manifestPath = Path.Combine(_gameRoot, "Mods", mod.FolderName, "mod.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = manifestPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Failed to open mod.json: {ex.Message}", false);
            }
        }
    }

    private void CtxEnable_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod != null && !mod.IsEnabled)
        {
            mod.IsEnabled = true;
        }
    }

    private void CtxDisable_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod != null && mod.IsEnabled)
        {
            mod.IsEnabled = false;
        }
    }

    private void CtxMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null) return;

        int index = _allMods.IndexOf(mod);
        if (index <= 0) return; // Already at top

        // Swap in _allMods
        _allMods.RemoveAt(index);
        _allMods.Insert(index - 1, mod);

        // Update load order indices
        for (int i = 0; i < _allMods.Count; i++)
            _allMods[i].LoadOrder = i;

        // Re-detect conflicts (order affects who "wins")
        _conflictDetector.DetectConflicts(_allMods);

        // Refresh display
        ApplySearchFilter();
        
        // Re-select the moved item
        LstMods.SelectedItem = mod;

        MarkPendingChanges();
        SetStatus($"Moved '{mod.Name}' up in load order", true);
    }

    private void CtxMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null) return;

        int index = _allMods.IndexOf(mod);
        if (index < 0 || index >= _allMods.Count - 1) return; // Already at bottom

        // Swap in _allMods
        _allMods.RemoveAt(index);
        _allMods.Insert(index + 1, mod);

        // Update load order indices
        for (int i = 0; i < _allMods.Count; i++)
            _allMods[i].LoadOrder = i;

        // Re-detect conflicts (order affects who "wins")
        _conflictDetector.DetectConflicts(_allMods);

        // Refresh display
        ApplySearchFilter();
        
        // Re-select the moved item
        LstMods.SelectedItem = mod;

        MarkPendingChanges();
        SetStatus($"Moved '{mod.Name}' down in load order", true);
    }

    private async void CtxViewConflicts_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null) return;

        if (mod.ConflictingFiles.Count == 0)
        {
            await new RPGModder.UI.Dialogs.MessageBox("Clean", "No conflicts detected.").ShowDialog(this);
            return;
        }

        // Generate the full report dynamically
        var fullReport = _conflictDetector.GenerateReport(_allMods);

        // Filter: Get only conflicts that involve THIS mod
        var myConflicts = fullReport.Conflicts
            .Where(c => c.Mods.Contains(mod.Name))
            .ToList();

        // Launch the Ultimate Viewer
        var viewer = new RPGModder.UI.Dialogs.ConflictViewerWindow(mod.Name, myConflicts);
        await viewer.ShowDialog(this);
    }

    #endregion

    #region Mod Profiles

    private void InitializeProfiles()
    {
        RefreshProfileList();
        _currentProfileName = CboProfiles.SelectedItem?.ToString() ?? "Default";
        CboProfiles.SelectionChanged += CboProfiles_SelectionChanged;
        BtnAddProfile.Click += BtnAddProfile_Click;
        BtnSaveProfile.Click += BtnSaveProfile_Click;
        BtnRemoveProfile.Click += BtnRemoveProfile_Click;
    }

    private void RefreshProfileList()
    {
        if (string.IsNullOrEmpty(_gameRoot)) return;

        var profiles = Directory.GetFiles(_gameRoot, "mod_profile_*.json")
                                .Select(f => Path.GetFileNameWithoutExtension(f).Replace("mod_profile_", ""))
                                .ToList();

        if (!profiles.Contains("Default")) profiles.Insert(0, "Default");

        var current = CboProfiles.SelectedItem?.ToString() ?? "Default";

        CboProfiles.ItemsSource = profiles;
        CboProfiles.SelectedItem = profiles.Contains(current) ? current : "Default";
    }

    private void CboProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboProfiles.SelectedItem is string profileName)
        {
            // Prevent reloading if clicking the same one
            if (profileName == _currentProfileName) return;

            LoadProfile(profileName);
            RefreshModList();
        }
    }

    private async void BtnAddProfile_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new RPGModder.UI.Dialogs.TextInputDialog("New Profile", "Enter profile name (e.g. Hardcore)");
        var result = await dialog.ShowDialog<bool>(this);

        if (result && !string.IsNullOrWhiteSpace(dialog.ResultText))
        {
            string cleanName = string.Join("", dialog.ResultText.Split(Path.GetInvalidFileNameChars()));

            // Save current configuration as this new profile
            // Use _allMods for LoadOrder (all mods in order) and filter for EnabledMods
            _profile.EnabledMods = _allMods.Where(m => m.IsEnabled).Select(m => m.FolderName).ToList();
            _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();

            string path = Path.Combine(_gameRoot, $"mod_profile_{cleanName}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(_profile, Formatting.Indented));

            RefreshProfileList();
            CboProfiles.SelectedItem = cleanName;

            SetStatus($"Profile '{cleanName}' created.", true);
        }
    }

    private async void BtnSaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        string currentProfile = CboProfiles.SelectedItem?.ToString() ?? "Default";
        string filename = currentProfile == "Default" ? "profile.json" : $"mod_profile_{currentProfile}.json";
        string path = Path.Combine(_gameRoot, filename);

        try
        {
            // 1. Capture current state
            _profile.EnabledMods = InstalledMods
                .Where(m => m.IsEnabled)
                .Select(m => m.FolderName)
                .ToList();

            _profile.LoadOrder = InstalledMods
                .Select(m => m.FolderName)
                .ToList();

            // 2. Write to disk
            File.WriteAllText(path, JsonConvert.SerializeObject(_profile, Formatting.Indented));

            var msg = new RPGModder.UI.Dialogs.MessageBox("Success", $"Profile '{currentProfile}' saved successfully!");
            await msg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save profile: {ex.Message}", false);
        }
    }

    private async void BtnRemoveProfile_Click(object? sender, RoutedEventArgs e)
    {
        string currentProfile = CboProfiles.SelectedItem?.ToString() ?? "Default";

        if (currentProfile == "Default")
        {
            await new RPGModder.UI.Dialogs.MessageBox("Error", "You cannot delete the Default profile.").ShowDialog(this);
            return;
        }

        var confirm = new RPGModder.UI.Dialogs.MessageBox("Confirm Delete",
            $"Are you sure you want to delete profile '{currentProfile}'?\nThis will switch you back to Default.", true);

        var result = await confirm.ShowDialog<bool>(this);

        if (result)
        {
            string filename = $"mod_profile_{currentProfile}.json";
            string path = Path.Combine(_gameRoot, filename);

            if (File.Exists(path)) File.Delete(path);

            // Reload list and force Default
            RefreshProfileList();
            CboProfiles.SelectedItem = "Default";

            SetStatus($"Profile '{currentProfile}' deleted.", true);
        }
    }


    private void LoadProfile(string newProfileName)
    {
        if (_engine == null) return;

        // 1. SAVE SWAP
        if (_currentProfileName != newProfileName)
        {
            try
            {
                // Only swap if the engine is ready
                if (!string.IsNullOrEmpty(_gameRoot))
                {
                    SetStatus($"Swapping saves: {_currentProfileName} ➡ {newProfileName}...", true);
                    _engine.SwapSaveFiles(_currentProfileName, newProfileName);
                    _currentProfileName = newProfileName;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error swapping saves: {ex.Message}", false);
                // Important: If swap fails, we might want to stop here to prevent
                // loading the wrong mod list on top of the wrong saves.
                return;
            }
        }

        // 2. Load JSON (Existing Logic)
        string filename = newProfileName == "Default" ? "profile.json" : $"mod_profile_{newProfileName}.json";
        string path = Path.Combine(_gameRoot, filename);

        if (File.Exists(path)) {
            try {
                _profile = JsonConvert.DeserializeObject<ModProfile>(File.ReadAllText(path)) ?? new ModProfile();
            } catch { _profile = new ModProfile(); }
        } else {
            _profile = new ModProfile();
        }

        RefreshModList();
        SetStatus($"Profile switched to '{newProfileName}'.", true);
    }

    #endregion

    #region Save Manager

    private async void BtnSaveManager_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine == null || string.IsNullOrEmpty(_gameRoot)) return;

        // Pass game root and "usesWww" flag to locate saves correctly
        var win = new RPGModder.UI.Dialogs.SaveManagerWindow(_gameRoot, _engine.UsesWwwFolder);
        await win.ShowDialog(this);
    }

    #endregion
    #region Drag-Drop Reordering

    private ModListItem? _draggedMod;

    private async void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 1. Validate Input
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed)
            return;

        if (sender is not Control dragHandle) return;

        // 2. Find the DataContext (The ModListItem)
        // Since the TextBlock is inside the DataTemplate, its DataContext is the ModListItem
        if (dragHandle.DataContext is not ModListItem modItem) return;

        // 3. Initiate Drag
        _draggedMod = modItem;
        var dataObject = new DataObject();
        dataObject.Set("ModListItem", modItem);

        // We use the DragHandle as the visual source
        await DragDrop.DoDragDrop(e, dataObject, DragDropEffects.Move);

        _draggedMod = null;
    }

    private void LstMods_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.Data.Contains("ModListItem"))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void LstMods_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("ModListItem")) return;

        var draggedMod = e.Data.Get("ModListItem") as ModListItem;
        if (draggedMod == null) return;

        // Find drop target
        var point = e.GetPosition(LstMods);
        var targetMod = GetModItemAtPoint(point);

        if (targetMod == null || targetMod == draggedMod) return;

        // Get indices
        int fromIndex = _allMods.IndexOf(draggedMod);
        int toIndex = _allMods.IndexOf(targetMod);

        if (fromIndex < 0 || toIndex < 0) return;

        // Move the item
        _allMods.RemoveAt(fromIndex);
        _allMods.Insert(toIndex, draggedMod);

        // Update load order indices
        for (int i = 0; i < _allMods.Count; i++)
            _allMods[i].LoadOrder = i;

        // Re-detect conflicts
        _conflictDetector.DetectConflicts(_allMods);

        // Refresh display
        ApplySearchFilter();

        // Re-select the moved item
        LstMods.SelectedItem = draggedMod;

        MarkPendingChanges();
        SetStatus($"Moved '{draggedMod.Name}' to position {toIndex + 1}", true);
    }

    private ModListItem? GetModItemAtPoint(Avalonia.Point point)
    {
        // Try to find which item is at this point
        foreach (var mod in InstalledMods)
        {
            var container = LstMods.ContainerFromItem(mod);
            if (container != null)
            {
                var bounds = container.Bounds;
                if (bounds.Contains(point))
                    return mod;
            }
        }
        return null;
    }

    #endregion

    #region Context Menu Remove

    private async void CtxRemove_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null) return;

        // Simulate the remove button click
        await RemoveModAsync(mod);
    }

    private async System.Threading.Tasks.Task RemoveModAsync(ModListItem modItem)
    {
        string modPath = Path.Combine(_gameRoot, "Mods", modItem.FolderName);

        try
        {
            _profile.EnabledMods.Remove(modItem.FolderName);
            SaveProfile();

            if (Directory.Exists(modPath))
                await System.Threading.Tasks.Task.Run(() => Directory.Delete(modPath, true));

            modItem.PropertyChanged -= ModItem_PropertyChanged;
            _allMods.Remove(modItem);
            InstalledMods.Remove(modItem);

            UpdateModCounts();
            MarkPendingChanges();
            SetStatus($"Removed: {modItem.Name}", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to remove: {ex.Message}", false);
        }
    }

    #endregion

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

        string backupPath = Path.Combine(_gameRoot, "ModManager_Backups");
        if (!Directory.Exists(backupPath))
        {
            SetStatus("No backup exists to reset.", false);
            return;
        }

        try
        {
            SetStatus("Resetting vanilla backup...", true);
            await System.Threading.Tasks.Task.Run(() => Directory.Delete(backupPath, true));
            
            // Re-initialize
            if (_engine != null)
            {
                _engine = new ModEngine(_gameExePath);
                await System.Threading.Tasks.Task.Run(() => _engine.InitializeSafeState());
            }

            SetStatus("Vanilla backup reset. A fresh backup will be created.", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to reset backup: {ex.Message}", false);
        }
    }

    #endregion

    #region Nexus Mods

    private async void InitializeNexusState()
    {
        // Bind game results
        LstNexusGames.ItemsSource = NexusGameResults;
        
        // Load saved API key
        if (!string.IsNullOrEmpty(_settings.Settings.NexusApiKey))
        {
            TxtNexusApiKey.Text = _settings.Settings.NexusApiKey;
            await ValidateAndConnectNexus(_settings.Settings.NexusApiKey);
        }

        // Check nxm protocol registration
        UpdateNxmRegistrationStatus();
    }

    private void UpdateNxmRegistrationStatus()
    {
        bool isRegistered = NxmProtocolHandler.IsProtocolRegistered();
        TxtNxmStatus.Text = isRegistered ? "Registered ✓" : "Not registered";
        BtnRegisterNxm.Content = isRegistered ? "Unregister" : "Register";
        _settings.Settings.NxmProtocolRegistered = isRegistered;
    }

    private async void BtnNexusConnect_Click(object? sender, RoutedEventArgs e)
    {
        // Show a simple input dialog or use the settings key
        var apiKey = TxtNexusApiKey.Text?.Trim();
        
        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("Enter your Nexus API key in Settings first.", false);
            return;
        }

        await ValidateAndConnectNexus(apiKey);
    }

    private async Task ValidateAndConnectNexus(string apiKey)
    {
        SetStatus("Connecting to Nexus Mods...", true);
        NexusAuthIndicator.Background = new SolidColorBrush(Color.Parse("#FFB900")); // Yellow = connecting
        TxtNexusStatus.Text = "Connecting...";

        var result = await _nexus.AuthenticateAsync(apiKey);

        if (result.Success && result.User != null)
        {
            NexusAuthIndicator.Background = new SolidColorBrush(Color.Parse("#4EC9B0")); // Green
            TxtNexusStatus.Text = $"Connected as {result.User.Name}" + (result.User.IsPremium ? " ⭐" : "");
            BtnNexusConnect.Content = "Connected";
            BtnNexusConnect.IsEnabled = false;

            // Enable search if we have a linked game
            bool hasLinkedGame = _currentNexusGame != null;
            TxtNexusSearch.IsEnabled = hasLinkedGame;
            BtnNexusSearch.IsEnabled = hasLinkedGame;
            BtnLinkNexusGame.IsEnabled = !string.IsNullOrEmpty(_gameExePath);
            
            // Enable category buttons
            BtnNexusAll.IsEnabled = hasLinkedGame;
            BtnNexusLatest.IsEnabled = hasLinkedGame;
            BtnNexusTrending.IsEnabled = hasLinkedGame;
            BtnNexusUpdated.IsEnabled = hasLinkedGame;
            BtnNexusRefresh.IsEnabled = hasLinkedGame;

            // Save the API key
            _settings.Settings.NexusApiKey = apiKey;
            _settings.Save();

            SetStatus($"Connected to Nexus as {result.User.Name}", true);
            
            if (hasLinkedGame)
            {
                TxtNexusResults.Text = "Search for mods above, or browse categories";
                await LoadNexusModsAsync("all");
            }
            else if (!string.IsNullOrEmpty(_gameExePath))
            {
                TxtNexusResults.Text = "Click 🔗 to link your game to Nexus";
            }
            else
            {
                TxtNexusResults.Text = "Load a game first, then link it to browse mods";
            }
        }
        else
        {
            NexusAuthIndicator.Background = new SolidColorBrush(Color.Parse("#F44747")); // Red
            TxtNexusStatus.Text = "Connection failed";
            BtnNexusConnect.Content = "Retry";
            BtnNexusConnect.IsEnabled = true;

            SetStatus($"Nexus connection failed: {result.Error}", false);
        }
    }

    private string _currentNexusCategory = "all";

    private async Task LoadNexusModsAsync(string category)
    {
        var gameDomain = GetSelectedNexusGameDomain();
        if (string.IsNullOrEmpty(gameDomain))
        {
            TxtNexusResults.Text = "Link your game to Nexus to browse mods";
            return;
        }

        // Cancel any in-flight Nexus request
        _nexusCts?.Cancel();
        _nexusCts = new CancellationTokenSource();
        var ct = _nexusCts.Token;

        _currentNexusCategory = category;
        UpdateCategoryButtonStyles();

        TxtNexusResults.Text = $"Loading {category} mods...";
        // Don't clear NexusMods here — keep old results visible as loading indicator

        NexusSearchResult result;
        string categoryLabel;

        try
        {
            switch (category)
            {
                case "trending":
                    result = await _nexus.GetTrendingModsAsync(gameDomain, ct);
                    categoryLabel = "trending";
                    break;
                case "updated":
                    result = await _nexus.GetUpdatedModsAsync(gameDomain, ct);
                    categoryLabel = "recently updated";
                    break;
                case "all":
                    result = await _nexus.GetAllModsCombinedAsync(gameDomain, ct);
                    categoryLabel = "all";
                    break;
                case "latest":
                default:
                    result = await _nexus.GetLatestModsAsync(gameDomain, ct);
                    categoryLabel = "latest";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return; // Silently abort — a newer request replaced this one
        }

        // Only update UI if this request wasn't cancelled
        if (ct.IsCancellationRequested) return;

        NexusMods.Clear();
        if (result.Success)
        {
            foreach (var mod in result.Mods)
            {
                NexusMods.Add(mod);
            }
            TxtNexusResults.Text = $"Showing {NexusMods.Count} {categoryLabel} mods for {GetSelectedNexusGameName()}";
        }
        else
        {
            TxtNexusResults.Text = $"Failed to load mods: {result.Error}";
        }
    }

    private void UpdateCategoryButtonStyles()
    {
        var activeColor = new SolidColorBrush(Color.Parse("#4EC9B0"));
        var inactiveColor = new SolidColorBrush(Color.Parse("#333"));
        var activeFg = new SolidColorBrush(Color.Parse("#1E1E1E"));
        var inactiveFg = new SolidColorBrush(Color.Parse("#EEE"));

        BtnNexusAll.Background = _currentNexusCategory == "all" ? activeColor : inactiveColor;
        BtnNexusAll.Foreground = _currentNexusCategory == "all" ? activeFg : inactiveFg;
        
        BtnNexusLatest.Background = _currentNexusCategory == "latest" ? activeColor : inactiveColor;
        BtnNexusLatest.Foreground = _currentNexusCategory == "latest" ? activeFg : inactiveFg;
        
        BtnNexusTrending.Background = _currentNexusCategory == "trending" ? activeColor : inactiveColor;
        BtnNexusTrending.Foreground = _currentNexusCategory == "trending" ? activeFg : inactiveFg;
        
        BtnNexusUpdated.Background = _currentNexusCategory == "updated" ? activeColor : inactiveColor;
        BtnNexusUpdated.Foreground = _currentNexusCategory == "updated" ? activeFg : inactiveFg;
    }

    private async void BtnNexusAll_Click(object? sender, RoutedEventArgs e)
    {
        await LoadNexusModsAsync("all");
    }

    private async void BtnNexusLatest_Click(object? sender, RoutedEventArgs e)
    {
        await LoadNexusModsAsync("latest");
    }

    private async void BtnNexusTrending_Click(object? sender, RoutedEventArgs e)
    {
        await LoadNexusModsAsync("trending");
    }

    private async void BtnNexusUpdated_Click(object? sender, RoutedEventArgs e)
    {
        await LoadNexusModsAsync("updated");
    }

    private async void BtnNexusRefresh_Click(object? sender, RoutedEventArgs e)
    {
        await LoadNexusModsAsync(_currentNexusCategory);
    }

    private async Task LoadRecentNexusMods()
    {
        await LoadNexusModsAsync("all");
    }

    private async void BtnNexusSearch_Click(object? sender, RoutedEventArgs e)
    {
        await SearchNexusMods();
    }

    private async void TxtNexusSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchNexusMods();
        }
    }

    private async Task SearchNexusMods()
    {
        if (!_nexus.IsAuthenticated) return;

        var query = TxtNexusSearch.Text?.Trim() ?? "";
        var gameDomain = GetSelectedNexusGameDomain();

        if (string.IsNullOrEmpty(gameDomain))
        {
            SetStatus("Link your game to Nexus first.", false);
            return;
        }

        // Cancel any in-flight request
        _nexusCts?.Cancel();
        _nexusCts = new CancellationTokenSource();
        var ct = _nexusCts.Token;

        TxtNexusResults.Text = "Searching...";

        NexusSearchResult result;
        try
        {
            result = await _nexus.SearchModsAsync(gameDomain, query, ct: ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;

        NexusMods.Clear();

        if (result.Success)
        {
            foreach (var mod in result.Mods.Take(50))
            {
                NexusMods.Add(mod);
            }
        }

        if (string.IsNullOrEmpty(query))
        {
            TxtNexusResults.Text = $"Showing {NexusMods.Count} recent mods";
        }
        else if (NexusMods.Count == 0 && result.IsClientSideSearch)
        {
            // No results from client-side filtering — mod may still exist on Nexus
            TxtNexusResults.Text = $"No mods matching \"{query}\" in cached results. Try browsing on Nexus directly.";
        }
        else
        {
            TxtNexusResults.Text = result.IsClientSideSearch
                ? $"Found {NexusMods.Count} mods matching \"{query}\" (limited search — browse Nexus for full results)"
                : $"Found {NexusMods.Count} mods matching \"{query}\"";
        }
    }

    private string GetSelectedNexusGameDomain()
    {
        return _currentNexusGame?.NexusDomain ?? "";
    }

    private string GetSelectedNexusGameName()
    {
        return _currentNexusGame?.NexusGameName ?? _detectedGameName;
    }

    // Game linking handlers
    private void BtnLinkNexusGame_Click(object? sender, RoutedEventArgs e)
    {
        // Show the link panel
        NexusLinkPanel.IsVisible = true;
        NexusGameResultsPanel.IsVisible = false;
        TxtNexusGameSearch.Text = _detectedGameName;
        TxtNexusGameSearch.Focus();
    }

    private void BtnCancelLinkGame_Click(object? sender, RoutedEventArgs e)
    {
        NexusLinkPanel.IsVisible = false;
        NexusGameResultsPanel.IsVisible = false;
    }

    private async void TxtNexusGameSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchNexusGames();
        }
    }

    private async void BtnSearchNexusGame_Click(object? sender, RoutedEventArgs e)
    {
        await SearchNexusGames();
    }

    private async Task SearchNexusGames()
    {
        if (!_nexus.IsAuthenticated)
        {
            SetStatus("Connect to Nexus first.", false);
            return;
        }

        var query = TxtNexusGameSearch.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            SetStatus("Enter a game name to search.", false);
            return;
        }

        SetStatus($"Searching for '{query}' on Nexus...", true);
        NexusGameResults.Clear();

        var games = await _nexus.SearchGamesAsync(query);

        foreach (var game in games.Take(10))
        {
            NexusGameResults.Add(game);
        }

        if (NexusGameResults.Count > 0)
        {
            NexusLinkPanel.IsVisible = false;
            NexusGameResultsPanel.IsVisible = true;
            SetStatus($"Found {NexusGameResults.Count} games. Select one to link.", true);
        }
        else
        {
            SetStatus($"No games found matching '{query}'.", false);
        }
    }

    private async void BtnSelectNexusGame_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NexusGame game) return;

        // Save the mapping
        _currentNexusGame = new NexusGameMapping
        {
            NexusDomain = game.DomainName,
            NexusGameName = game.Name,
            NexusGameId = game.Id
        };

        // Save to settings
        if (!string.IsNullOrEmpty(_gameExePath))
        {
            _settings.Settings.NexusGameMappings[_gameExePath] = _currentNexusGame;
            _settings.Save();
        }

        // Update UI
        NexusGameResultsPanel.IsVisible = false;
        UpdateNexusGameDisplay();

        // Enable search and category buttons
        TxtNexusSearch.IsEnabled = true;
        BtnNexusSearch.IsEnabled = true;
        BtnNexusAll.IsEnabled = true;
        BtnNexusLatest.IsEnabled = true;
        BtnNexusTrending.IsEnabled = true;
        BtnNexusUpdated.IsEnabled = true;
        BtnNexusRefresh.IsEnabled = true;

        SetStatus($"Linked to {game.Name} on Nexus!", true);
        await LoadNexusModsAsync("all");
    }

    private void UpdateNexusGameDisplay()
    {
        if (_currentNexusGame != null)
        {
            TxtNexusGameName.Text = _currentNexusGame.NexusGameName;
            NexusGameIndicator.Background = new SolidColorBrush(Color.Parse("#4EC9B0")); // Green
        }
        else if (!string.IsNullOrEmpty(_detectedGameName))
        {
            TxtNexusGameName.Text = $"{_detectedGameName} (not linked)";
            NexusGameIndicator.Background = new SolidColorBrush(Color.Parse("#FFB900")); // Yellow
        }
        else
        {
            TxtNexusGameName.Text = "No game loaded";
            NexusGameIndicator.Background = new SolidColorBrush(Color.Parse("#F44747")); // Red
        }
    }

    // Called when a game is loaded - tries to detect and link to Nexus
    private async Task DetectNexusGame()
    {
        if (string.IsNullOrEmpty(_gameExePath)) return;

        // Try to detect game name from folder or package.json
        _detectedGameName = DetectGameName();

        // Check if we have a saved mapping
        if (_settings.Settings.NexusGameMappings.TryGetValue(_gameExePath, out var savedMapping))
        {
            _currentNexusGame = savedMapping;
            UpdateNexusGameDisplay();
            
            if (_nexus.IsAuthenticated)
            {
                TxtNexusSearch.IsEnabled = true;
                BtnNexusSearch.IsEnabled = true;
                BtnNexusAll.IsEnabled = true;
                BtnNexusLatest.IsEnabled = true;
                BtnNexusTrending.IsEnabled = true;
                BtnNexusUpdated.IsEnabled = true;
                BtnNexusRefresh.IsEnabled = true;
                await LoadNexusModsAsync("all");
            }
            return;
        }

        // Try to auto-detect from known games
        var knownDomain = NexusApiService.GetKnownGameDomain(_detectedGameName);
        if (knownDomain != null && _nexus.IsAuthenticated)
        {
            // Try to find the game on Nexus
            var game = await _nexus.FindGameByNameAsync(_detectedGameName);
            if (game != null)
            {
                _currentNexusGame = new NexusGameMapping
                {
                    NexusDomain = game.DomainName,
                    NexusGameName = game.Name,
                    NexusGameId = game.Id
                };

                _settings.Settings.NexusGameMappings[_gameExePath] = _currentNexusGame;
                _settings.Save();

                SetStatus($"Auto-linked to {game.Name} on Nexus!", true);
            }
        }

        UpdateNexusGameDisplay();
        BtnLinkNexusGame.IsEnabled = _nexus.IsAuthenticated;
        
        if (_currentNexusGame != null && _nexus.IsAuthenticated)
        {
            TxtNexusSearch.IsEnabled = true;
            BtnNexusSearch.IsEnabled = true;
            BtnNexusAll.IsEnabled = true;
            BtnNexusLatest.IsEnabled = true;
            BtnNexusTrending.IsEnabled = true;
            BtnNexusUpdated.IsEnabled = true;
            BtnNexusRefresh.IsEnabled = true;
            await LoadNexusModsAsync("all");
        }
    }

    private string DetectGameName()
    {
        // Try package.json first
        var packageJsonPath = Path.Combine(_gameRoot, "package.json");
        if (!File.Exists(packageJsonPath))
            packageJsonPath = Path.Combine(_gameRoot, "www", "package.json");

        if (File.Exists(packageJsonPath))
        {
            try
            {
                var json = File.ReadAllText(packageJsonPath);
                dynamic? pkg = JsonConvert.DeserializeObject(json);
                var name = pkg?.name?.ToString() ?? pkg?.productName?.ToString();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch { }
        }

        // Fall back to folder name
        return Path.GetFileName(_gameRoot) ?? "Unknown Game";
    }

    private void BtnViewOnNexus_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NexusMod mod) return;

        var gameDomain = mod.DomainName ?? GetSelectedNexusGameDomain();
        if (string.IsNullOrEmpty(gameDomain))
        {
            SetStatus("Could not determine game domain.", false);
            return;
        }

        var modUrl = $"https://www.nexusmods.com/{gameDomain}/mods/{mod.ModId}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = modUrl,
                UseShellExecute = true
            });
            SetStatus($"Opening Nexus page for {mod.Name}...", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open browser: {ex.Message}", false);
        }
    }

    private async void BtnNexusDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NexusMod mod) return;

        if (!_nexus.IsAuthenticated)
        {
            SetStatus("Connect to Nexus first.", false);
            return;
        }

        // Get the mod's files
        var gameDomain = mod.DomainName ?? GetSelectedNexusGameDomain();
        SetStatus($"Fetching files for {mod.Name}...", true);

        var files = await _nexus.GetModFilesAsync(gameDomain, mod.ModId);

        if (files.Count == 0)
        {
            SetStatus("No files found for this mod.", false);
            return;
        }

        // Get the primary/main file
        var mainFile = files.FirstOrDefault(f => f.IsPrimary) ?? files.First();

        // Check if user is premium (can get direct download links)
        var downloadLinks = await _nexus.GetDownloadLinksAsync(gameDomain, mod.ModId, mainFile.FileId);

        if (downloadLinks.Count > 0 && !string.IsNullOrEmpty(downloadLinks[0].Uri))
        {
            // Premium user - can download directly
            await DownloadAndInstallMod(downloadLinks[0].Uri, mainFile.FileName ?? $"{mod.Name}.zip", mod);
        }
        else
        {
            // Non-premium - open in browser for manual download
            var modUrl = $"https://www.nexusmods.com/{gameDomain}/mods/{mod.ModId}?tab=files";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = modUrl,
                    UseShellExecute = true
                });
                SetStatus($"Opening Nexus page for {mod.Name}. Download and install manually, or use 'Download with Manager' button.", true);
            }
            catch
            {
                SetStatus("Could not open browser. Visit nexusmods.com to download.", false);
            }
        }
    }

    private async Task DownloadAndInstallMod(string downloadUrl, string fileName, NexusMod mod)
    {
        SetStatus($"Downloading {mod.Name}...", true);
        DownloadsPanel.IsVisible = true;

        try
        {
            // Use DownloadManager for proper queuing, progress, and cancellation
            var downloadItem = await _downloadManager.QueueDownloadAsync(downloadUrl, fileName, mod);

            // Subscribe to progress updates from the download manager
            void OnProgress(DownloadItem item)
            {
                if (item.Id != downloadItem.Id) return;
                Dispatcher.UIThread.Post(() =>
                {
                    TxtDownloadCount.Text = $"({item.ProgressFormatted}) {item.SpeedFormatted}";
                });
            }

            void OnCompleted(DownloadItem item)
            {
                if (item.Id != downloadItem.Id) return;
                _downloadManager.DownloadProgress -= OnProgress;
                _downloadManager.DownloadCompleted -= OnCompleted;
                _downloadManager.DownloadFailed -= OnFailed;
            }

            void OnFailed(DownloadItem item)
            {
                if (item.Id != downloadItem.Id) return;
                _downloadManager.DownloadProgress -= OnProgress;
                _downloadManager.DownloadCompleted -= OnCompleted;
                _downloadManager.DownloadFailed -= OnFailed;
                Dispatcher.UIThread.Post(() =>
                {
                    SetStatus($"Download failed for {mod.Name}: {item.Error}", false);
                    DownloadsPanel.IsVisible = false;
                    TxtDownloadCount.Text = "(0)";
                });
            }

            _downloadManager.DownloadProgress += OnProgress;
            _downloadManager.DownloadCompleted += OnCompleted;
            _downloadManager.DownloadFailed += OnFailed;

            // Wait for the download to finish
            while (downloadItem.Status == DownloadStatus.Queued ||
                   downloadItem.Status == DownloadStatus.Downloading)
            {
                await Task.Delay(200);
            }

            string? filePath = downloadItem.Status == DownloadStatus.Completed
                ? downloadItem.DestinationPath
                : null;

            if (filePath != null && File.Exists(filePath))
            {
                // Auto-install if a game is loaded
                if (_engine != null)
                {
                    // Check if we already have this mod installed (by Nexus ID)
                    var existingMod = InstalledMods.FirstOrDefault(m => m.Manifest.Metadata.NexusId == mod.ModId);

                    string statusMsg;
                    InstallResult result;
                    string modsDir = Path.Combine(_gameRoot, "Mods");
                    Directory.CreateDirectory(modsDir);

                    if (existingMod != null)
                    {
                        // UPDATE existing mod
                        statusMsg = $"Updating {mod.Name} (v{mod.Version})...";
                        SetStatus(statusMsg, true);

                        result = await System.Threading.Tasks.Task.Run(() =>
                            _installer.InstallModWithNexusInfo(
                                filePath, modsDir, mod.ModId, mod.Version, existingMod.FolderName));
                    }
                    else
                    {
                        // NEW INSTALL
                        statusMsg = $"Installing {mod.Name} (v{mod.Version})...";
                        SetStatus(statusMsg, true);

                        result = await System.Threading.Tasks.Task.Run(() =>
                            _installer.InstallModWithNexusInfo(
                                filePath, modsDir, mod.ModId, mod.Version));
                    }

                    if (result.Success && result.FolderName != null)
                    {
                        if (!_profile.EnabledMods.Contains(result.FolderName))
                        {
                            _profile.EnabledMods.Add(result.FolderName);
                            SaveProfile();
                        }

                        RefreshModList();
                        MarkPendingChanges();
                        SetStatus($"✓ {statusMsg.Replace("...", "")} Complete!", true);
                    }
                    else
                    {
                        SetStatus($"Installation failed: {result.Error}", false);
                    }
                }
                else
                {
                    SetStatus($"Downloaded to: {filePath}. Load a game to install.", true);
                }
            }
            else
            {
                SetStatus($"Download failed for {mod.Name}.", false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Download error: {ex.Message}", false);
        }
        finally
        {
            DownloadsPanel.IsVisible = false;
            TxtDownloadCount.Text = "(0)";
        }
    }

    private void BtnSaveNexusKey_Click(object? sender, RoutedEventArgs e)
    {
        var apiKey = TxtNexusApiKey.Text?.Trim();
        
        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("API key cannot be empty.", false);
            return;
        }

        _settings.Settings.NexusApiKey = apiKey;
        _settings.Save();
        SetStatus("API key saved. Click 'Connect' on the Nexus Mods tab to connect.", true);
    }

    private void BtnRegisterNxm_Click(object? sender, RoutedEventArgs e)
    {
        bool isCurrentlyRegistered = NxmProtocolHandler.IsProtocolRegistered();

        if (isCurrentlyRegistered)
        {
            // Unregister
            if (NxmProtocolHandler.UnregisterProtocol())
            {
                SetStatus("nxm:// protocol unregistered.", true);
            }
            else
            {
                SetStatus("Failed to unregister protocol.", false);
            }
        }
        else
        {
            // Register
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath))
            {
                SetStatus("Could not determine application path.", false);
                return;
            }

            if (NxmProtocolHandler.RegisterProtocol(exePath))
            {
                SetStatus("nxm:// protocol registered! You can now use 'Download with Manager' on Nexus.", true);
            }
            else
            {
                SetStatus("Failed to register protocol. Try running as administrator.", false);
            }
        }

        UpdateNxmRegistrationStatus();
        _settings.Save();
    }

    #endregion

    #region Helpers

    private void SetStatus(string message, bool success)
    {
        TxtStatus.Text = message;
        StatusIndicator.Background = success
            ? new SolidColorBrush(Color.Parse("#4EC9B0"))
            : new SolidColorBrush(Color.Parse("#F44747"));
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
