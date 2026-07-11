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
#region Nexus Mods

    private async void InitializeNexusState()
    {
        // Bind game results
        LstNexusGames.ItemsSource = NexusGameResults;

        // Load saved API key
        if (!string.IsNullOrEmpty(_settings.Settings.NexusApiKey))
        {
            var ssoService = new NexusSsoService(NexusAppSlug);
            string storedKey = _settings.Settings.NexusApiKey;
            string plainKey = ssoService.DecryptKeyFromStorage(storedKey);

            if (!string.IsNullOrEmpty(plainKey))
            {
                if (!ssoService.IsSecureStoredValue(storedKey))
                {
                    if (!TryPersistNexusKey(ssoService, plainKey, _settings.Settings.IsSsoAuthenticated))
                    {
                        _settings.Settings.NexusApiKey = null;
                        _settings.Save();
                        SetStatus("The legacy Nexus key is available for this session but was removed from settings because secure storage is unavailable.", false);
                    }
                }

                TxtNexusApiKey.Text = plainKey;
                await ValidateAndConnectNexus(plainKey);

                // If they have a key but it wasn't via SSO, show the migration banner
                if (!_settings.Settings.IsSsoAuthenticated)
                {
                    BdrMigrationBanner.IsVisible = true;
                }
                else
                {
                    // UI Polish: Make it look nice for SSO users
                    TxtNexusApiKey.PasswordChar = '\0';
                    TxtNexusApiKey.Text = "Connected via One-Click SSO";
                    TxtNexusApiKey.IsReadOnly = true;
                    TxtNexusApiKey.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
                    BtnSaveNexusKey.IsVisible = false;
                    BtnDisconnectNexus.IsVisible = true;
                }
            }
        }

        // Check nxm protocol registration
        UpdateNxmRegistrationStatus();
    }

    private void UpdateNxmRegistrationStatus()
    {
        bool isRegistered = NxmProtocolHandler.IsProtocolRegistered();
        TxtNxmStatus.Text = isRegistered ? "Registered" : "Not registered";
        BtnRegisterNxm.Content = isRegistered ? "Unregister" : "Register";
        _settings.Settings.NxmProtocolRegistered = isRegistered;
    }

    private async void BtnNexusConnect_Click(object? sender, RoutedEventArgs e)
    {
        // Disable button to prevent spamming
        BtnNexusConnect.IsEnabled = false;
        
        if (Application.Current?.Resources.TryGetResource("WarningBrush", null, out var warningBrush) == true)
            NexusAuthIndicator.Background = (IBrush)warningBrush!;

        // Use the SSO Service with your requested app slug
        var ssoService = new NexusSsoService(NexusAppSlug);

        string? rawApiKey = await ssoService.AuthenticateAsync(status =>
        {
            // The WebSocket runs on a background thread, so we MUST marshal UI updates back to the main thread
            Dispatcher.UIThread.Post(() =>
            {
                TxtNexusStatus.Text = status;
                SetStatus(status, true);
            });
        });

        if (!string.IsNullOrEmpty(rawApiKey))
        {
            bool persisted = TryPersistNexusKey(ssoService, rawApiKey, isSsoAuthenticated: true);

            // Hide migration banner since they just migrated
            BdrMigrationBanner.IsVisible = false;

            // Update the settings tab textbox to look nice for SSO users
            TxtNexusApiKey.PasswordChar = '\0';
            TxtNexusApiKey.Text = "Connected via One-Click SSO";
            TxtNexusApiKey.IsReadOnly = true;
            TxtNexusApiKey.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
            BtnSaveNexusKey.IsVisible = false;
            BtnDisconnectNexus.IsVisible = true;

            // Proceed to validate and fetch user info
            await ValidateAndConnectNexus(rawApiKey);
            if (!persisted)
                SetStatus("Connected for this session, but the API key could not be saved securely.", false);
        }
        else
        {
            if (Application.Current?.Resources.TryGetResource("DangerBrush", null, out var dangerBrush) == true)
                NexusAuthIndicator.Background = (IBrush)dangerBrush!;
            
            TxtNexusStatus.Text = "Login failed or timed out.";
            BtnNexusConnect.IsEnabled = true;
            SetStatus("Nexus SSO authentication failed or was cancelled.", false);
        }
    }

    private async System.Threading.Tasks.Task ValidateAndConnectNexus(string apiKey)
    {
        SetStatus("Connecting to Nexus Mods...", true);
        TxtNexusStatus.Text = "Authenticating...";

        var result = await _nexus.AuthenticateAsync(apiKey);

        if (result.Success && result.User != null)
        {
            if (Application.Current?.Resources.TryGetResource("SuccessBrush", null, out var successBrush) == true)
                NexusAuthIndicator.Background = (IBrush)successBrush!;
            
            TxtNexusStatus.Text = $"Connected as {result.User.Name}" + (result.User.IsPremium ? " (Premium)" : "");
            BtnNexusConnect.Content = "Connected";
            BtnNexusConnect.IsEnabled = false;

            // Enable search if we have a linked game
            bool hasLinkedGame = _currentNexusGame != null;
            TxtNexusSearch.IsEnabled = hasLinkedGame;
            BtnNexusSearch.IsEnabled = hasLinkedGame;
            BtnLinkNexusGame.IsEnabled = !string.IsNullOrEmpty(_gameExePath);

            // Enable category buttons
            CmbNexusCategories.IsEnabled = hasLinkedGame;
            CmbNexusSort.IsEnabled = hasLinkedGame;
            BtnNexusRefresh.IsEnabled = hasLinkedGame;

            SetStatus($"Connected to Nexus as {result.User.Name}", true);

            if (hasLinkedGame)
            {
                TxtNexusResults.Text = "Search for mods above, or browse categories";
                await FetchCategoriesForGame();
                await LoadNexusModsAsync(false);
            }
            else if (!string.IsNullOrEmpty(_gameExePath))
            {
                TxtNexusResults.Text = "Link your game to its Nexus Mods page.";
            }
            else
            {
                TxtNexusResults.Text = "Load a game first, then link it to browse mods";
            }
        }
        else
        {
            if (Application.Current?.Resources.TryGetResource("DangerBrush", null, out var dangerBrush) == true)
                NexusAuthIndicator.Background = (IBrush)dangerBrush!;
            
            TxtNexusStatus.Text = "Connection failed";
            BtnNexusConnect.Content = "Connect"; // Changed from Retry to Connect for SSO
            BtnNexusConnect.IsEnabled = true;

            SetStatus($"Nexus connection failed: {result.Error}", false);
        }
    }

    // ==========================================================
    // NEXUS V2 BROWSE & PAGINATION ENGINE
    // ==========================================================
    private bool _isProgrammaticCategoryChange = false;
    private int _currentOffset = 0;
    private bool _hasNextPage = false;
    private bool _isLoadingData = false;
    private bool _isSearching = false;

    private async Task FetchCategoriesForGame()
    {
        var domain = GetSelectedNexusGameDomain();
        if (string.IsNullOrEmpty(domain)) return;

        var categories = await _nexus.GetCategoriesAsync(domain);

        _isProgrammaticCategoryChange = true;
        var allCats = new List<NexusCategory> { new NexusCategory { CategoryId = -1, Name = "All Categories" } };
        allCats.AddRange(categories);

        CmbNexusCategories.ItemsSource = allCats;
        CmbNexusCategories.SelectedIndex = 0;
        CmbNexusCategories.IsEnabled = true;
        CmbNexusSort.IsEnabled = true;
        BtnNexusRefresh.IsEnabled = true;
        _isProgrammaticCategoryChange = false;
    }

    private async Task LoadNexusModsAsync(bool loadMore = false)
    {
        if (_isLoadingData && loadMore) return;
        _isLoadingData = true;

        var gameDomain = GetSelectedNexusGameDomain();
        if (string.IsNullOrEmpty(gameDomain))
        {
            TxtNexusResults.Text = "Link your game to Nexus to browse mods";
            _isLoadingData = false;
            return;
        }

        if (!loadMore)
        {
            _nexusCts?.Cancel();
            _nexusCts = new CancellationTokenSource();
            _currentOffset = 0; // Changed from _currentCursor = null
            NexusMods.Clear();
            _isSearching = false;
        }

        TxtNexusResults.Text = loadMore ? "Loading more mods..." : "Loading mods...";

        string? categoryName = (CmbNexusCategories.SelectedItem as NexusCategory)?.Name;
        if (categoryName == "All Categories") categoryName = null;

        string sortStr = (CmbNexusSort.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Trending";

        try
        {
            // Pass the categoryName string into the updated API method
            var result = await _nexus.BrowseModsAsync(gameDomain, sortStr, categoryName, _currentOffset, _nexusCts!.Token);

            if (_nexusCts.Token.IsCancellationRequested) return;

            if (result.Success)
            {
                foreach (var mod in result.Mods) NexusMods.Add(mod);
                _currentOffset = result.NextOffset; // Changed from NextCursor
                _hasNextPage = result.HasNextPage;
                TxtNexusResults.Text = $"Showing {NexusMods.Count} {sortStr.ToLower()} mods for {GetSelectedNexusGameName()}";
            }
            else
            {
                TxtNexusResults.Text = $"Failed to load mods: {result.Error}";
            }
        }
        catch (OperationCanceledException) { }
        finally { _isLoadingData = false; }
    }

    private async Task SearchNexusMods(bool loadMore = false)
    {
        if (_isLoadingData && loadMore) return;
        _isLoadingData = true;

        var query = TxtNexusSearch.Text?.Trim() ?? "";
        var gameDomain = GetSelectedNexusGameDomain();

        if (string.IsNullOrEmpty(gameDomain)) return;

        if (!loadMore)
        {
            _nexusCts?.Cancel();
            _nexusCts = new CancellationTokenSource();
            _currentOffset = 0;
            NexusMods.Clear();
            _isSearching = true;
        }

        TxtNexusResults.Text = loadMore ? "Searching more..." : "Searching...";

        try
        {
            var result = await _nexus.SearchModsAsync(gameDomain, query, _currentOffset, _nexusCts!.Token);

            if (_nexusCts.Token.IsCancellationRequested) return;

            if (result.Success)
            {
                foreach (var mod in result.Mods) NexusMods.Add(mod);
                _currentOffset = result.NextOffset;
                _hasNextPage = result.HasNextPage;
                TxtNexusResults.Text = $"Found {NexusMods.Count} mods matching \"{query}\"";
            }
            else
            {
                TxtNexusResults.Text = $"Search failed: {result.Error}";
            }
        }
        catch (OperationCanceledException) { }
        finally { _isLoadingData = false; }
    }

    private async void BtnNexusSearch_Click(object? sender, RoutedEventArgs e) => await SearchNexusMods(false);
    private async void TxtNexusSearch_KeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SearchNexusMods(false); }

    private void LstNexusMods_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isLoadingData || !_hasNextPage) return;

        if (e.Source is ScrollViewer scrollViewer)
        {
            if (scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 100)
            {
                if (_isSearching)
                {
                    _ = SearchNexusMods(true);
                }
                else
                {
                    _ = LoadNexusModsAsync(true);
                }
            }
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
        BtnNexusRefresh.IsEnabled = true;
        SetStatus($"Linked to {game.Name} on Nexus!", true);

        await FetchCategoriesForGame();
        await LoadNexusModsAsync(false);
    }

    private void UpdateNexusGameDisplay()
    {
        if (_currentNexusGame != null)
        {
            TxtNexusGameName.Text = _currentNexusGame.NexusGameName;
            if (Application.Current?.Resources.TryGetResource("SuccessBrush", null, out var successBrush) == true)
                NexusGameIndicator.Background = (IBrush)successBrush!;
        }
        else if (!string.IsNullOrEmpty(_detectedGameName))
        {
            TxtNexusGameName.Text = $"{_detectedGameName} (not linked)";
            if (Application.Current?.Resources.TryGetResource("WarningBrush", null, out var warningBrush) == true)
                NexusGameIndicator.Background = (IBrush)warningBrush!;
        }
        else
        {
            TxtNexusGameName.Text = "No game loaded";
            if (Application.Current?.Resources.TryGetResource("DangerBrush", null, out var dangerBrush) == true)
                NexusGameIndicator.Background = (IBrush)dangerBrush!;
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

                await FetchCategoriesForGame();
                await LoadNexusModsAsync(false);
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
            BtnNexusRefresh.IsEnabled = true;

            await FetchCategoriesForGame();
            await LoadNexusModsAsync(false);
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
                string? name = pkg?.name?.ToString() ?? pkg?.productName?.ToString();
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
                    var existingMod = _allMods.FirstOrDefault(m => m.Manifest.Metadata.NexusId == mod.ModId);

                    string statusMsg;
                    InstallResult result;
        string modsDir = RequireWorkspace().Mods;
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
            SetStatus($"{statusMsg.Replace("...", "")} complete.", true);
                    }
                    else
                    {
                        SetStatus($"Installation failed: {result.Error}", false);
                    }

                    // Instantly delete the ZIP file from the TempDownloads folder after processing
                    try { File.Delete(filePath); } catch { /* Ignore file-in-use locks */ }
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

    private async void BtnSaveNexusKey_Click(object? sender, RoutedEventArgs e)
    {
        var apiKey = TxtNexusApiKey.Text?.Trim();

        if (string.IsNullOrEmpty(apiKey))
        {
            SetStatus("API key cannot be empty.", false);
            return;
        }

        var ssoService = new NexusSsoService(NexusAppSlug);
        if (!TryPersistNexusKey(ssoService, apiKey, isSsoAuthenticated: false))
            return;

        SetStatus("API key saved. Validating connection...", true);
        await ValidateAndConnectNexus(apiKey);
    }

    private void BtnDisconnectNexus_Click(object? sender, RoutedEventArgs e)
    {
        string? storedKey = _settings.Settings.NexusApiKey;
        new NexusSsoService(NexusAppSlug).DeleteStoredKey(storedKey);
        _settings.Settings.NexusApiKey = null;
        _settings.Settings.IsSsoAuthenticated = false;
        _settings.Save();

        // Clear API instance state instead of destroying the HttpClient
        _nexus.ClearAuth(); 
        
        // Revert UI Polish
        TxtNexusApiKey.Text = "";
        TxtNexusApiKey.IsReadOnly = false;
        TxtNexusApiKey.Foreground = new SolidColorBrush(Color.Parse("#DDD"));
        TxtNexusApiKey.PasswordChar = '●';
        BtnSaveNexusKey.IsVisible = true;
        BtnDisconnectNexus.IsVisible = false;

        // Reset Auth UI in Nexus Tab
        if (Application.Current?.Resources.TryGetResource("DangerBrush", null, out var dangerBrush) == true)
            NexusAuthIndicator.Background = (IBrush)dangerBrush!;
        
        TxtNexusStatus.Text = "Not connected";
        BtnNexusConnect.Content = "Login with Nexus Mods";
        BtnNexusConnect.IsEnabled = true;

        SetStatus("Disconnected from Nexus Mods.", true);
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
                SetStatus(OperatingSystem.IsWindows()
                    ? "Failed to register protocol. Check application permissions."
                    : "Failed to register protocol. Ensure xdg-utils is installed and your user applications directory is writable.", false);
            }
        }

        UpdateNxmRegistrationStatus();
        _settings.Save();
    }

    private bool TryPersistNexusKey(NexusSsoService service, string rawKey, bool isSsoAuthenticated)
    {
        try
        {
            _settings.Settings.NexusApiKey = service.EncryptKeyForStorage(rawKey);
            _settings.Settings.IsSsoAuthenticated = isSsoAuthenticated;
            _settings.Save();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            _settings.Settings.NexusApiKey = null;
            _settings.Settings.IsSsoAuthenticated = false;
            _settings.Save();
            SetStatus(ex.Message, false);
            return false;
        }
    }

    #endregion

    
}

