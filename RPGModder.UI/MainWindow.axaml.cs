using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace RPGModder.UI;

public partial class MainWindow : Window
{
    private ModEngine? _engine;
    private ModProfile _profile = new();
    private string _gameRoot = "";

    // UI Binding
    public ObservableCollection<ModManifest> InstalledMods { get; set; } = new();

    public MainWindow()
    {
        InitializeComponent();
        LstMods.ItemsSource = InstalledMods;

        // Event Wiring
        BtnSelect.Click += BtnSelect_Click;
        DropZone.AddHandler(DragDrop.DropEvent, DropZone_Drop);
        DropZone.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
    }

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

            // Validate Engine (MV or MZ)
            bool isMV = File.Exists(Path.Combine(dir, "js", "rpg_core.js"));
            bool isMZ = File.Exists(Path.Combine(dir, "js", "rmmz_core.js"));

            if (!isMV && !isMZ)
            {
                TxtStatus.Text = "Error: Invalid game directory (core .js file missing).";
                return;
            }

            // Success! Load everything.
            _gameRoot = dir;
            TxtGamePath.Text = path;

            _engine = new ModEngine(path);

            // 1. Create the 'Clean Backup' if it doesn't exist
            TxtStatus.Text = "Initializing Safe State... (This may take a moment)";
            _engine.InitializeSafeState();

            // 2. Load existing profile (Enabled Mods)
            LoadProfile();

            // 3. Refresh UI
            RefreshModList();

            TxtStatus.Text = "Engine loaded. Safe Mode Active.";
            PlaceholderText.IsVisible = InstalledMods.Count == 0;
        }
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        if (_engine != null && e.Data.Contains(DataFormats.Files))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (_engine == null) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        foreach (var item in files)
        {
            string sourcePath = item.Path.LocalPath;
            if (Directory.Exists(sourcePath))
            {
                ImportMod(sourcePath);
            }
        }
    }

    private async System.Threading.Tasks.Task ImportMod(string sourceFolder)
    {
        string manifestPath = Path.Combine(sourceFolder, "mod.json");
        if (!File.Exists(manifestPath))
        {
            TxtStatus.Text = $"Error: 'mod.json' missing in {Path.GetFileName(sourceFolder)}";
            return;
        }

        try
        {
            string json = File.ReadAllText(manifestPath);
            var manifest = JsonConvert.DeserializeObject<ModManifest>(json);
            if (manifest == null) return;

            // Use DirectoryInfo to handle trailing slashes correctly
            string folderName = new DirectoryInfo(sourceFolder).Name;

            // Safety: If for some reason name is still empty, use the Mod ID or a timestamp
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = manifest.Metadata.Id ?? $"Mod_{DateTime.Now.Ticks}";

            string libPath = Path.Combine(_gameRoot, "Mods", folderName);

            if (Directory.Exists(libPath)) Directory.Delete(libPath, true);
            CopyDirectory(sourceFolder, libPath);

            if (!_profile.EnabledMods.Contains(folderName))
            {
                _profile.EnabledMods.Add(folderName);
                SaveProfile();
            }

            TxtStatus.Text = "Rebuilding Game...";
            await System.Threading.Tasks.Task.Run(() => _engine!.RebuildGame(_profile));

            RefreshModList();
            TxtStatus.Text = $"Successfully installed: {manifest.Metadata.Name}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private void RefreshModList()
    {
        InstalledMods.Clear();
        string modsRoot = Path.Combine(_gameRoot, "Mods");
        if (!Directory.Exists(modsRoot)) return;

        // Read all folders in Mods/ directory
        foreach (var dir in Directory.GetDirectories(modsRoot))
        {
            string jsonPath = Path.Combine(dir, "mod.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    var m = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(jsonPath));
                    if (m != null) InstalledMods.Add(m);
                }
                catch { /* Ignore bad json */ }
            }
        }
        PlaceholderText.IsVisible = InstalledMods.Count == 0;
    }

    // --- PROFILE MANAGEMENT ---

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
        string profilePath = Path.Combine(_gameRoot, "profile.json");
        File.WriteAllText(profilePath, JsonConvert.SerializeObject(_profile, Formatting.Indented));
    }

    private void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
    }
}