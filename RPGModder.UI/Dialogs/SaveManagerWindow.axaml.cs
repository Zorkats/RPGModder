using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RPGModder.UI.Dialogs;

public partial class SaveManagerWindow : Window
{
    private string _saveFolder = string.Empty;
    private string _backupFolder = string.Empty;
    public ObservableCollection<SaveFileItem> Saves { get; set; } = new();

    public SaveManagerWindow()
    {
        InitializeComponent();
        LstSaves.ItemsSource = Saves;
    }

    public SaveManagerWindow(string gameRoot, bool isWww) : this()
    {
        // 1. Locate Saves (Standard vs Root)
        string potentialPath = Path.Combine(gameRoot, isWww ? "www" : "", "save");
        if (!Directory.Exists(potentialPath))
            potentialPath = gameRoot;

        _saveFolder = potentialPath;
        _backupFolder = Path.Combine(gameRoot, "ModManager_Backups", "Saves");

        if (!Directory.Exists(_backupFolder)) Directory.CreateDirectory(_backupFolder);

        TxtPath.Text = $"Save Location: {_saveFolder}";
        LstSaves.ItemsSource = Saves;

        RefreshList();
    }

    private void RefreshList()
    {
        Saves.Clear();
        if (!Directory.Exists(_saveFolder)) return;

        // Support both MV (.rpgsave) and MZ (.rmmzsave)
        var files = Directory.GetFiles(_saveFolder, "file*.rpgsave")
            .Concat(Directory.GetFiles(_saveFolder, "*.rmmzsave"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime);

        foreach (var f in files)
        {
            Saves.Add(new SaveFileItem
            {
                FileName = f.Name,
                FullPath = f.FullName,
                LastModified = f.LastWriteTime.ToString("g")
            });
        }
    }

    private void BtnBackup_Click(object? sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        var item = btn?.Tag as SaveFileItem;

        if (item == null) return; // Guard clause

        // Now we safely use 'item'
        string dest = Path.Combine(_backupFolder, $"{item.FileName}_{DateTime.Now:MMdd_HHmm}.bak");
        File.Copy(item.FullPath, dest, true);

        new MessageBox("Success", "Backup created!").ShowDialog(this);
    }

    private async void BtnRestore_Click(object? sender, RoutedEventArgs e)
    {
        // Robust Casting
        var btn = sender as Button;
        var item = btn?.Tag as SaveFileItem;

        if (item == null) return; // Guard clause

        // 1. Find backups
        var backups = Directory.GetFiles(_backupFolder, $"{item.FileName}_*.bak")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        if (backups.Count == 0)
        {
            await new MessageBox("No Backups", $"No backups found for {item.FileName}").ShowDialog(this);
            return;
        }

        var latest = backups.First();

        var confirm = new MessageBox("Restore Backup",
            $"Found {backups.Count} backup(s).\n\n" +
            $"Restoring LATEST backup:\n" +
            $"📅 {latest.LastWriteTime}\n" +
            $"📄 {latest.Name}\n\n" +
            "Current save will be overwritten. Continue?",
            true);

        var result = await confirm.ShowDialog<bool>(this);

        if (result)
        {
            try
            {
                // Safety Backup
                string safetyPath = Path.Combine(_backupFolder, $"{item.FileName}_PRE_RESTORE_{DateTime.Now:MMdd_HHmm}.bak");
                File.Copy(item.FullPath, safetyPath, true);

                // Restore
                File.Copy(latest.FullName, item.FullPath, true);

                RefreshList();
                await new MessageBox("Success", "Restored! (Safety backup created)").ShowDialog(this);
            }
            catch (Exception ex)
            {
                await new MessageBox("Error", $"Failed: {ex.Message}").ShowDialog(this);
            }
        }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = _saveFolder, UseShellExecute = true }); } catch { }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}

public class SaveFileItem
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string LastModified { get; set; } = "";
}