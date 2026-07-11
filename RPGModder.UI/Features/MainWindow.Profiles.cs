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
#region Mod Profiles

    // --- State Persistence Helpers ---
    private string GetActiveProfileMarker()
    {
        if (string.IsNullOrEmpty(_gameRoot)) return "Default";
        string markerPath = RequireWorkspace().ActiveProfileMarker;
        if (File.Exists(markerPath))
        {
            try
            {
                return SafePathService.ValidateDirectoryName(
                    File.ReadAllText(markerPath).Trim(),
                    "Active profile marker");
            }
            catch (InvalidDataException)
            {
                return "Default";
            }
        }
        return "Default";
    }

    private void SetActiveProfileMarker(string profileName)
    {
        if (string.IsNullOrEmpty(_gameRoot)) return;
        profileName = SafePathService.ValidateDirectoryName(profileName, "Profile name");
        string markerPath = RequireWorkspace().ActiveProfileMarker;
        FileTreeService.WriteAllTextAtomic(markerPath, profileName);
    }

    // --- Core Data Methods ---
    private void LoadProfileDataOnly(string profileName)
    {
        string path = RequireWorkspace().GetProfilePath(profileName);

        if (File.Exists(path))
        {
            try { _profile = _profiles.Load(RequireWorkspace(), profileName); }
            catch { _profile = new ModProfile(); }
        }
        else
        {
            _profile = new ModProfile();
        }
    }

    private void SaveProfile()
    {
        _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();

        // FIX: Dynamically target the correct profile file instead of hardcoding "profile.json"
        _profiles.Save(RequireWorkspace(), _currentProfileName, _profile);
    }

    // --- UI & Swapping Logic ---
    private void InitializeProfiles()
    {
        RefreshProfileList();
        CboProfiles.SelectionChanged += CboProfiles_SelectionChanged;
        BtnAddProfile.Click += BtnAddProfile_Click;
        BtnSaveProfile.Click += BtnSaveProfile_Click;
        BtnRemoveProfile.Click += BtnRemoveProfile_Click;
    }

    private void RefreshProfileList()
    {
        if (string.IsNullOrEmpty(_gameRoot)) return;

        var profiles = _profiles.List(RequireWorkspace()).ToList();

        CboProfiles.ItemsSource = profiles;

        // Force UI to match internal state without triggering a reload
        if (profiles.Contains(_currentProfileName))
            CboProfiles.SelectedItem = _currentProfileName;
        else
            CboProfiles.SelectedItem = "Default";
    }

    private void CboProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboProfiles.SelectedItem is string profileName)
        {
            if (profileName == _currentProfileName) return;
            LoadProfile(profileName);
        }
    }

    private void LoadProfile(string newProfileName)
    {
        if (_engine == null) return;

        if (_currentProfileName != newProfileName)
        {
            try
            {
                if (!string.IsNullOrEmpty(_gameRoot))
                {
                    SetStatus($"Swapping saves from {_currentProfileName} to {newProfileName}...", true);
                    _engine.SwapSaveFiles(_currentProfileName, newProfileName);

                    _currentProfileName = newProfileName;
                    SetActiveProfileMarker(newProfileName);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error swapping saves: {ex.Message}", false);
                return;
            }
        }

        LoadProfileDataOnly(newProfileName);
        RefreshModList();
        SetStatus($"Profile switched to '{newProfileName}'.", true);
    }

    private async void BtnAddProfile_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("New Profile", "Enter profile name (e.g. Hardcore)");
        var resultName = await dialog.ShowDialog<string?>(this);

        if (!string.IsNullOrWhiteSpace(resultName))
        {
            string cleanName;
            try
            {
                cleanName = SafePathService.ValidateDirectoryName(resultName, "Profile name");
            }
            catch (InvalidDataException ex)
            {
                SetStatus(ex.Message, false);
                return;
            }

            _profile.EnabledMods = _allMods.Where(m => m.IsEnabled).Select(m => m.FolderName).ToList();
            _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();

            _profiles.Save(RequireWorkspace(), cleanName, _profile);

            RefreshProfileList();
            CboProfiles.SelectedItem = cleanName;

            SetStatus($"Profile '{cleanName}' created.", true);
        }
    }

    private async void BtnSaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _profile.EnabledMods = _allMods.Where(m => m.IsEnabled).Select(m => m.FolderName).ToList();
            _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();

            SaveProfile(); // Uses the newly fixed dynamic helper

            var msg = new RPGModder.UI.Dialogs.MessageBox("Success", $"Profile '{_currentProfileName}' saved successfully!");
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
            _profiles.Delete(RequireWorkspace(), currentProfile);

            RefreshProfileList();
            CboProfiles.SelectedItem = "Default";

            SetStatus($"Profile '{currentProfile}' deleted.", true);
        }
    }

    #endregion

}


