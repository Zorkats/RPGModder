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
using RPGModder.Core.Workspace;
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

    // --- Core Data Methods ---
    private void LoadProfileDataOnly(string profileName)
    {
        try
        {
            _profile = _profiles.Load(RequireWorkspace(), profileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Profile '{profileName}' could not be read and starts empty: {ex.Message}");
            _profile = new ModProfile();
        }
    }

    private void SaveProfile()
    {
        _profile.LoadOrder = _allMods.Select(m => m.FolderName).ToList();
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
            CboProfiles.SelectedItem = ProfileStore.DefaultProfileName;
    }

    private async void CboProfiles_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboProfiles.SelectedItem is string profileName && profileName != _currentProfileName)
        {
            await SwitchProfileAsync(profileName);
        }
    }

    /// <summary>
    /// Switches the live saves and the active profile through Core. On a refusal or a failure the
    /// selection goes back to the profile that is still active.
    /// </summary>
    private async Task<bool> SwitchProfileAsync(string newProfileName)
    {
        if (_gameSession == null) return false;

        SetStatus($"Swapping saves from {_currentProfileName} to {newProfileName}...", true);
        OperationResult result = await _profileWorkflow.SwitchAsync(_gameSession, _currentProfileName, newProfileName);
        if (!result.Success)
        {
            SetStatus($"Cannot switch profile: {FirstErrorMessage(result)}", false);
            CboProfiles.SelectedItem = _currentProfileName;
            return false;
        }

        _currentProfileName = newProfileName;
        LoadProfileDataOnly(newProfileName);
        RefreshModList();
        SetStatus($"Profile switched to '{newProfileName}'. Deploy it to apply its mods to the game.", true);
        return true;
    }

    private async void BtnAddProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_gameSession == null) return;

        var dialog = new TextInputDialog("New Profile", "Enter profile name (e.g. Hardcore)");
        var resultName = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(resultName)) return;

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

        SyncProfileFromList();
        OperationResult result = await _profileWorkflow.CreateAsync(_gameSession, cleanName, _profile);
        if (!result.Success)
        {
            SetStatus($"Cannot create profile: {FirstErrorMessage(result)}", false);
            return;
        }

        RefreshProfileList();
        CboProfiles.SelectedItem = cleanName;
        SetStatus($"Profile '{cleanName}' created.", true);
    }

    private async void BtnSaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SyncProfileFromList();
            SaveProfile();

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
        if (_gameSession == null) return;

        string currentProfile = CboProfiles.SelectedItem?.ToString() ?? ProfileStore.DefaultProfileName;
        if (currentProfile == ProfileStore.DefaultProfileName)
        {
            await new RPGModder.UI.Dialogs.MessageBox("Error", "You cannot delete the Default profile.").ShowDialog(this);
            return;
        }

        ConfirmChoice choice = await new ConfirmDialog(
            "Delete profile",
            $"Delete profile '{currentProfile}'? This switches you back to Default first.",
            "Delete").AskAsync(this);
        if (choice != ConfirmChoice.Confirm)
        {
            return;
        }

        // Switch away first, so a refused switch leaves the profile in place and still active.
        if (currentProfile == _currentProfileName
            && !await SwitchProfileAsync(ProfileStore.DefaultProfileName))
        {
            return;
        }

        OperationResult result = await _profileWorkflow.DeleteAsync(_gameSession, currentProfile);
        RefreshProfileList();
        SetStatus(
            result.Success ? $"Profile '{currentProfile}' deleted." : $"Cannot delete profile: {FirstErrorMessage(result)}",
            result.Success);
    }

    #endregion

}
