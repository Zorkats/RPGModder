using Avalonia.Controls;
using Avalonia.Interactivity;
using RPGModder.Core.Application;
using RPGModder.Core.Models;
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
    private readonly GameSession? _session;
    private readonly SaveBackupWorkflow? _saveBackups;
    public ObservableCollection<SaveFileItem> Saves { get; set; } = new();

    public SaveManagerWindow()
    {
        InitializeComponent();
        LstSaves.ItemsSource = Saves;
    }

    public SaveManagerWindow(GameSession session, SaveBackupWorkflow saveBackups) : this()
    {
        _session = session;
        _saveBackups = saveBackups;

        // Saves live in (www/)save, or next to the executable for games that keep them there.
        string potentialPath = Path.Combine(session.GameRoot, session.Engine.UsesWwwFolder ? "www" : "", "save");
        if (!Directory.Exists(potentialPath))
            potentialPath = session.GameRoot;

        _saveFolder = potentialPath;
        _backupFolder = session.Workspace.SaveBackups;

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

    private async void BtnBackup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SaveFileItem item || _session == null || _saveBackups == null) return;

        OperationResult result = await _saveBackups.BackupAsync(_session, item.FullPath);
        await new MessageBox(result.Success ? "Success" : "Backup failed", result.Success ? "Backup created!" : FirstError(result))
            .ShowDialog(this);
    }

    private static string FirstError(OperationResult result)
    {
        return result.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ?? "Unknown error.";
    }

    private async void BtnRestore_Click(object? sender, RoutedEventArgs e)
    {
        // Robust Casting
        var btn = sender as Button;
        var item = btn?.Tag as SaveFileItem;

        if (item == null) return; // Guard clause

        // 1. Find backups
        if (_session == null || _saveBackups == null) return;

        var backups = (Directory.Exists(_backupFolder)
                ? Directory.GetFiles(_backupFolder, $"{item.FileName}_*.bak")
                : Array.Empty<string>())
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        if (backups.Count == 0)
        {
            await new MessageBox("No Backups", $"No backups found for {item.FileName}").ShowDialog(this);
            return;
        }

        var latest = backups.First();

        ConfirmChoice choice = await new ConfirmDialog(
            "Restore Backup",
            $"Found {backups.Count} backup(s).\n\n" +
            $"Restoring LATEST backup:\n" +
            $"{latest.LastWriteTime}\n" +
            $"{latest.Name}\n\n" +
            $"{item.FileName} will be overwritten. A safety copy of it is kept.",
            "Restore").AskAsync(this);
        if (choice != ConfirmChoice.Confirm)
        {
            return;
        }

        OperationResult result = await _saveBackups.RestoreAsync(_session, item.FullPath, latest.FullName);
        if (!result.Success)
        {
            await new MessageBox("Error", $"Failed: {FirstError(result)}").ShowDialog(this);
            return;
        }

        RefreshList();
        await new MessageBox("Success", "Restored! (Safety backup created)").ShowDialog(this);
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _saveFolder, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"The save folder could not be opened: {ex.Message}");
            TxtPath.Text = $"Could not open the save folder: {ex.Message}";
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}

public class SaveFileItem
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string LastModified { get; set; } = "";
}
