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

        string modsDir = RequireWorkspace().Mods;
        Directory.CreateDirectory(modsDir);

        SetStatus($"Installing: {Path.GetFileName(path)}...", true);

        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
                _installer.InstallMod(path, modsDir));

            if (result.Success && result.FolderName != null)
            {
                _modCatalog.Invalidate();
                if (!_profile.EnabledMods.Contains(result.FolderName))
                {
                    _profile.EnabledMods.Add(result.FolderName);
                    SaveProfile();
                }

                RefreshModList();
                MarkPendingChanges();
                SetStatus($"Installed {result.Manifest?.Metadata.Name ?? result.FolderName}. Deploy changes to activate it.", true);
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

        ModCatalogSnapshot snapshot = _modCatalog.Load(RequireWorkspace(), _profile);
        foreach (ModListItem item in snapshot.Mods)
        {
            item.PropertyChanged += ModItem_PropertyChanged;
            _allMods.Add(item);
        }

        OperationDiagnostic? catalogError = snapshot.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (catalogError != null)
        {
            SetStatus($"{catalogError.Subject}: {catalogError.Message}", false);
        }

        // Detect conflicts
        _conflictDetector.DetectConflicts(_allMods);
        RefreshConflictWorkspace();

        // Apply current search filter
        ApplySearchFilter();
    }

    private void ModItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModListItem.IsEnabled))
        {
            MarkPendingChanges();
            UpdateModCounts();
            _conflictDetector.DetectConflicts(_allMods);
            RefreshConflictWorkspace();
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
            SetStatus("Cannot deploy while the game is running. Close it first.", false);
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

            if (_gameSession == null)
            {
                throw new InvalidOperationException("No game session is active.");
            }

            OperationResult result = await _gameSessions.DeployAsync(_gameSession, _profile);

            if (!result.Success)
            {
                string details = string.Join("\n", result.Diagnostics
                    .Where(item => item.Severity == DiagnosticSeverity.Error)
                    .Select(item => $"{item.Subject ?? "Deployment"}: {item.Message}"));
                SetStatus("Rebuild failed and the previous game state was restored.", false);
                await new RPGModder.UI.Dialogs.MessageBox("Deployment failed", details).ShowDialog(this);
                return;
            }

            EvaluateModLoaderEnvironment();

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
            RefreshDeploymentHistory();
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
        if (_engine != null && e.DataTransfer.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (_engine == null) return;

        var files = e.DataTransfer.TryGetFiles();
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

    
#region Helpers for Mod Updating

    private CancellationTokenSource? _updateCheckCts;

    private async Task RunBackgroundUpdateCheckAsync()
    {
        if (!_nexus.IsAuthenticated) return;

        _updateCheckCts?.Cancel();
        _updateCheckCts = new CancellationTokenSource();
        var token = _updateCheckCts.Token;

        // Give the UI 3 seconds to breathe before hammering the network
        await Task.Delay(3000, token);

        // Snapshot the list to prevent enumeration crashes if the user is moving mods
        var modsToCheck = _allMods.ToList();

        foreach (var mod in modsToCheck)
        {
            if (token.IsCancellationRequested) break;

            // Only check mods that have a valid Nexus ID in their mod.json
            if (mod.Manifest?.Metadata?.NexusId != null && mod.Manifest.Metadata.NexusId > 0)
            {
                try
                {
                    var gameDomain = _currentNexusGame?.NexusDomain ?? NexusApiService.GetKnownGameDomain(_detectedGameName);
                    if (string.IsNullOrEmpty(gameDomain)) continue;

                    var modInfo = await _nexus.GetModAsync(gameDomain, mod.Manifest.Metadata.NexusId.Value, token);

                    if (modInfo != null && VersionComparer.IsNewerVersion(mod.Version, modInfo.Version))
                    {
                        Dispatcher.UIThread.Post(() => {
                            mod.UpdateAvailable = true;
                            mod.LatestVersion = modInfo.Version;
                            // If this item is currently selected, reveal the context menu button
                            if (LstMods.SelectedItem == mod) CtxDownloadUpdate.IsVisible = true;
                        });
                    }
                }
                catch { /* Silent fail on network errors */ }

                // CRITICAL: 500ms delay to respect Nexus API rate limits
                await Task.Delay(500, token);
            }
        }
    }

    private async void CtxLinkNexus_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null) return;

        if (!_nexus.IsAuthenticated)
        {
            SetStatus("Connect to Nexus first to use the Mod Linker.", false);
            return;
        }

        var domain = _currentNexusGame?.NexusDomain ?? NexusApiService.GetKnownGameDomain(_detectedGameName);
        if (string.IsNullOrEmpty(domain))
        {
            SetStatus("Link your game to Nexus first to use the Mod Linker.", false);
            return;
        }

        var dialog = new ModLinkWindow(_nexus, domain, mod.Name);
        var result = await dialog.ShowDialog<bool>(this);

        if (result && dialog.SelectedMod != null)
        {
            mod.Manifest.Metadata.NexusId = dialog.SelectedMod.ModId;

            // Save to mod.json
        string modsDir = SafePathService.ResolveContainedPath(RequireWorkspace().Mods, mod.FolderName, "Mod folder");
            string jsonPath = Path.Combine(modsDir, "mod.json");

            if (File.Exists(jsonPath))
            {
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(mod.Manifest, Formatting.Indented));
                SetStatus($"Linked '{mod.Name}' to '{dialog.SelectedMod.Name}' (ID {dialog.SelectedMod.ModId})", true);

                // Trigger an immediate update check for this mod
                _ = RunBackgroundUpdateCheckAsync();
            }
        }
    }
    private async void CtxCheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null || mod.Manifest?.Metadata?.NexusId == null) return;

        if (!_nexus.IsAuthenticated)
        {
            SetStatus("Connect to Nexus first to check for updates.", false);
            return;
        }

        SetStatus($"Checking for updates for {mod.Name}...", true);
        await RunSingleUpdateCheckAsync(mod);
    }

    private async Task RunSingleUpdateCheckAsync(ModListItem mod)
    {
        if (mod.Manifest?.Metadata?.NexusId == null) return;

        try
        {
            var gameDomain = _currentNexusGame?.NexusDomain ?? NexusApiService.GetKnownGameDomain(_detectedGameName);
            if (string.IsNullOrEmpty(gameDomain))
            {
                SetStatus("Game not linked to Nexus. Update check aborted.", false);
                return;
            }

            var modInfo = await _nexus.GetModAsync(gameDomain, mod.Manifest.Metadata.NexusId.Value);

            if (modInfo != null)
            {
                if (VersionComparer.IsNewerVersion(mod.Version, modInfo.Version))
                {
                    mod.UpdateAvailable = true;
                    mod.LatestVersion = modInfo.Version;
                    if (LstMods.SelectedItem == mod) CtxDownloadUpdate.IsVisible = true;
                    SetStatus($"Update found for {mod.Name}: v{modInfo.Version}", true);
                }
                else
                {
                    mod.UpdateAvailable = false;
                    if (LstMods.SelectedItem == mod) CtxDownloadUpdate.IsVisible = false;
                    SetStatus($"{mod.Name} is already up to date (v{mod.Version}).", true);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Update check failed for {mod.Name}: {ex.Message}", false);
        }
    }

    private async void CtxDownloadUpdate_Click(object? sender, RoutedEventArgs e)
    {
        var mod = GetSelectedMod();
        if (mod == null || !mod.UpdateAvailable || mod.Manifest?.Metadata?.NexusId == null) return;

        var gameDomain = _currentNexusGame?.NexusDomain ?? NexusApiService.GetKnownGameDomain(_detectedGameName);
        if (string.IsNullOrEmpty(gameDomain)) return;

        SetStatus($"Preparing update for {mod.Name}...", true);

        // Fetch the newest files
        var files = await _nexus.GetModFilesAsync(gameDomain, mod.Manifest.Metadata.NexusId.Value);
        var mainFile = files.FirstOrDefault(f => f.IsPrimary) ?? files.FirstOrDefault();

        if (mainFile == null)
        {
            SetStatus("No files found on Nexus for this update.", false);
            return;
        }

        var links = await _nexus.GetDownloadLinksAsync(gameDomain, mod.Manifest.Metadata.NexusId.Value, mainFile.FileId);

        if (links.Count == 0)
        {
            SetStatus("Cannot download automatically (Nexus Premium required). Right-click and 'View on Nexus'.", false);
            return;
        }

        var nexusModInfo = new NexusMod { ModId = mod.Manifest.Metadata.NexusId.Value, Name = mod.Name, Version = mod.LatestVersion, DomainName = gameDomain };

        // Hide the update badge immediately
        mod.UpdateAvailable = false;
        if (LstMods.SelectedItem == mod) CtxDownloadUpdate.IsVisible = false;

        // Hand off to your existing installer, which will cleanly overwrite the old folder
        await DownloadAndInstallMod(links[0].Uri, mainFile.FileName ?? $"{mod.Name}.zip", nexusModInfo);
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

        string modPath = SafePathService.ResolveContainedPath(RequireWorkspace().Mods, mod.FolderName, "Mod folder");
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

        string manifestPath = Path.Combine(SafePathService.ResolveContainedPath(RequireWorkspace().Mods, mod.FolderName, "Mod folder"), "mod.json");
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
        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(ModListItemFormat, modItem.FolderName));

        // We use the DragHandle as the visual source
        await DragDrop.DoDragDropAsync(e, dataTransfer, DragDropEffects.Move);

        _draggedMod = null;
    }

    private void LstMods_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (e.DataTransfer.Contains(ModListItemFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void LstMods_Drop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(ModListItemFormat)) return;

        string? draggedFolderName = e.DataTransfer.TryGetValue(ModListItemFormat);
        var draggedMod = _allMods.FirstOrDefault(item =>
            item.FolderName.Equals(draggedFolderName, StringComparison.OrdinalIgnoreCase));
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
        string modPath = SafePathService.ResolveContainedPath(RequireWorkspace().Mods, modItem.FolderName, "Mod folder");

        try
        {
            _profile.EnabledMods.Remove(modItem.FolderName);
            SaveProfile();

            if (Directory.Exists(modPath))
                await System.Threading.Tasks.Task.Run(() => Directory.Delete(modPath, true));

            _modCatalog.Invalidate();

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

    
}

