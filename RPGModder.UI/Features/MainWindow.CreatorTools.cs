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
        string cleanBackup = RequireWorkspace().CleanBackup;
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

            var successBrush = (IBrush)Application.Current!.Resources["SuccessBrush"]!;
            var warningBrush = (IBrush)Application.Current!.Resources["WarningBrush"]!;
            var dangerBrush = (IBrush)Application.Current!.Resources["DangerBrush"]!;
            var primaryBrush = (IBrush)Application.Current!.Resources["PrimaryBrush"]!;

            foreach (var (path, _) in _currentAnalysis.NewFiles)
                DetectedChanges.Add(new ChangeItem("NEW", path, ((SolidColorBrush)successBrush).Color.ToString()));

            foreach (var (path, _) in _currentAnalysis.ModifiedFiles)
                DetectedChanges.Add(new ChangeItem("MOD", path, ((SolidColorBrush)warningBrush).Color.ToString()));

            foreach (var (path, _) in _currentAnalysis.JsonPatches)
                DetectedChanges.Add(new ChangeItem("PATCH", path, ((SolidColorBrush)primaryBrush).Color.ToString()));

            foreach (var plugin in _currentAnalysis.NewPlugins)
                DetectedChanges.Add(new ChangeItem("PLUGIN", plugin.Name, "#9C27B0")); // Keep purple for plugins

            foreach (var warning in _currentAnalysis.Warnings)
                DetectedChanges.Add(new ChangeItem("WARN", warning, ((SolidColorBrush)warningBrush).Color.ToString()));

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

            TxtPackerStatus.Text = "Package created.";

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

    
}

