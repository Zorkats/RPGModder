using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RPGModder.Core.Models;

// Wraps a ModManifest with UI state (enabled/disabled, folder reference, conflicts)
public class ModListItem : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _hasConflicts;
    private string _conflictTooltip = "";
    private List<string> _conflictingFiles = new();
    private List<string> _conflictingMods = new();
    private int _loadOrder;

    public ModManifest Manifest { get; set; }
    public string FolderName { get; set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasConflicts
    {
        get => _hasConflicts;
        set
        {
            if (_hasConflicts != value)
            {
                _hasConflicts = value;
                OnPropertyChanged();
            }
        }
    }

    public string ConflictTooltip
    {
        get => _conflictTooltip;
        set
        {
            if (_conflictTooltip != value)
            {
                _conflictTooltip = value;
                OnPropertyChanged();
            }
        }
    }

    public List<string> ConflictingFiles
    {
        get => _conflictingFiles;
        set
        {
            _conflictingFiles = value;
            OnPropertyChanged();
        }
    }

    public List<string> ConflictingMods
    {
        get => _conflictingMods;
        set
        {
            _conflictingMods = value;
            OnPropertyChanged();
        }
    }

    public int LoadOrder
    {
        get => _loadOrder;
        set
        {
            if (_loadOrder != value)
            {
                _loadOrder = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set
        {
            if (_updateAvailable != value)
            {
                _updateAvailable = value;
                OnPropertyChanged();
            }
        }
    }

    private string _latestVersion = "";
    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (_latestVersion != value)
            {
                _latestVersion = value;
                OnPropertyChanged();
            }
        }
    }

    // Convenience accessors for XAML binding
    public string Name => Manifest?.Metadata?.Name ?? "Unknown";
    public string Author => Manifest?.Metadata?.Author ?? "Unknown";
    public string Version => Manifest?.Metadata?.Version ?? "1.0";
    public string Description => Manifest?.Metadata?.Description ?? "";

    // Detects if this mod deploys files into FHMM's isolated execution environments
    public bool IsFhmmMod => Manifest?.Metadata?.IsFhmmMod ?? false;

    // Get all files this mod touches
    public HashSet<string> GetAffectedFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Manifest == null) return files;

        foreach (var op in Manifest.FileOps)
        {
            if (!string.IsNullOrEmpty(op.Target))
                files.Add(NormalizePath(op.Target));
        }

        foreach (var patch in Manifest.JsonPatches)
        {
            if (!string.IsNullOrEmpty(patch.Target))
                files.Add(NormalizePath(patch.Target));
        }

        return files;
    }

    public IReadOnlyList<ModFileIntent> GetFileIntents()
    {
        var intents = new List<ModFileIntent>();
        foreach (FileOperation operation in Manifest.FileOps)
        {
            if (!string.IsNullOrWhiteSpace(operation.Target))
            {
                intents.Add(new ModFileIntent(NormalizePath(operation.Target), ModFileIntentKind.Replace));
            }
        }

        foreach (JsonPatch patch in Manifest.JsonPatches)
        {
            if (!string.IsNullOrWhiteSpace(patch.Target))
            {
                intents.Add(new ModFileIntent(NormalizePath(patch.Target), ModFileIntentKind.JsonPatch));
            }
        }

        return intents;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    public ModListItem(ModManifest manifest, string folderName, bool isEnabled)
    {
        Manifest = manifest;
        FolderName = folderName;
        _isEnabled = isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum ModFileIntentKind
{
    Replace,
    JsonPatch
}

public sealed record ModFileIntent(string Target, ModFileIntentKind Kind);
