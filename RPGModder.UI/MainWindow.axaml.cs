using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using RPGModder.Core.Application;
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
using Avalonia;
using RPGModder.UI.Dialogs;

namespace RPGModder.UI;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> ModListItemFormat =
        DataFormat.CreateStringApplicationFormat("RPGModder.ModListItem");
    private ModEngine? _engine;
    private ModProfile _profile = new();
    private string _currentProfileName = "Default";
    private string _gameRoot = "";
    private string _gameExePath = "";
    private bool _hasPendingChanges = false;
    private GameWorkspacePaths? _workspacePaths;
    private GameSession? _gameSession;


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
    private readonly IModCatalogService _modCatalog = new ModCatalogService();
    private readonly IProfileRepository _profiles = new ProfileRepository();
    private readonly IDeploymentHistoryService _deploymentHistory = new DeploymentHistoryService();
    private readonly IGameSessionService _gameSessions = new GameSessionService();
    private const string NexusAppSlug = "zorkats2-rpgmodder";
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
    public ObservableCollection<FileConflict> ActiveConflicts { get; set; } = new();
    public ObservableCollection<DeploymentHistoryEntry> DeploymentHistory { get; set; } = new();

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
        LstAllConflicts.ItemsSource = ActiveConflicts;
        LstActivity.ItemsSource = DeploymentHistory;

        // My Mods tab events
        BtnSelect.Click += BtnSelect_Click;
        BtnScanGames.Click += BtnScanGames_Click;
        CmbDetectedGames.SelectionChanged += CmbDetectedGames_SelectionChanged;
        MenuInstallZip.Click += MenuInstallZip_Click;
        MenuInstallFolder.Click += MenuInstallFolder_Click;
        BtnLaunchGame.Click += BtnLaunchGame_Click;
        BtnRebuild.Click += BtnRebuild_Click;
        BtnFhmmHelp.Click += BtnFhmmHelp_Click;
        BtnTyHelp.Click += BtnTyHelp_Click;
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

        // Initialize Mod Profiles
        InitializeProfiles();

        // Phase 1.2: Search
        TxtSearch.TextChanged += TxtSearch_TextChanged;
        BtnClearSearch.Click += BtnClearSearch_Click;

        // Phase 1.2: Context menu
        CtxOpenFolder.Click += CtxOpenFolder_Click;
        CtxViewManifest.Click += CtxViewManifest_Click;
        CtxViewConflicts.Click += CtxViewConflicts_Click;
        CtxLinkNexus.Click += CtxLinkNexus_Click;
        CtxCheckUpdate.Click += CtxCheckUpdate_Click;
        CtxDownloadUpdate.Click += CtxDownloadUpdate_Click;
        LstMods.SelectionChanged += (s, e) => {
            if (LstMods.SelectedItem is ModListItem mod) {
                CtxDownloadUpdate.IsVisible = mod.UpdateAvailable;
                CtxCheckUpdate.IsVisible = mod.Manifest?.Metadata?.NexusId != null && mod.Manifest.Metadata.NexusId > 0;
                ModInspector.DataContext = mod;
                ModInspector.IsVisible = true;
                ModInspectorPlaceholder.IsVisible = false;
            }
            else
            {
                ModInspector.DataContext = null;
                ModInspector.IsVisible = false;
                ModInspectorPlaceholder.IsVisible = true;
            }
        };
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
        BtnSaveNexusKey.Click += BtnSaveNexusKey_Click;
        BtnDisconnectNexus.Click += BtnDisconnectNexus_Click;
        BtnUpgradeSso.Click += (s, e) => BtnNexusConnect_Click(s, e);
        BtnDismissMigration.Click += (s, e) => BdrMigrationBanner.IsVisible = false;
        BtnRegisterNxm.Click += BtnRegisterNxm_Click;
        BtnLinkNexusGame.Click += BtnLinkNexusGame_Click;
        BtnSearchNexusGame.Click += BtnSearchNexusGame_Click;
        BtnCancelLinkGame.Click += BtnCancelLinkGame_Click;
        TxtNexusGameSearch.KeyDown += TxtNexusGameSearch_KeyDown;
        BtnNexusSearch.Click += BtnNexusSearch_Click;
        TxtNexusSearch.KeyDown += TxtNexusSearch_KeyDown;
        CmbNexusCategories.SelectionChanged += (s, e) => { if (!_isProgrammaticCategoryChange) _ = LoadNexusModsAsync(false); };
        CmbNexusSort.SelectionChanged += (s, e) => _ = LoadNexusModsAsync(false);
        BtnNexusRefresh.Click += async (s, e) => await LoadNexusModsAsync(false);
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
}
