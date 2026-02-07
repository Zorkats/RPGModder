using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using System.Collections.Generic;
using System.Linq;

namespace RPGModder.UI.Dialogs;

public partial class ConflictViewerWindow : Window
{
    private List<ConflictViewModel> _allItems = new();

    public ConflictViewerWindow() { InitializeComponent(); }

    public ConflictViewerWindow(string myModName, List<FileConflict> conflicts) : this()
    {
        TxtSummary.Text = $"Analysis for: {myModName}";

        // Transform the raw conflict data into UI-friendly rows
        _allItems = conflicts.Select(c =>
        {
            bool isWinner = c.Winner == myModName;

            // Who are we fighting? (Everyone except me)
            var enemies = c.Mods.Where(m => m != myModName).ToList();
            string enemiesStr = string.Join(", ", enemies);

            return new ConflictViewModel
            {
                FilePath = c.FilePath,
                Status = isWinner ? "WINNING" : "LOSING",
                StatusColor = isWinner ? Brushes.LightGreen : Brushes.IndianRed,
                Details = isWinner
                    ? $"Overwrites: {enemiesStr}"
                    : $"Overwritten by: {c.Winner}"
            };
        }).OrderBy(x => x.Status).ThenBy(x => x.FilePath).ToList();

        TxtCount.Text = $"{_allItems.Count} total conflicts detected";
        LstConflicts.ItemsSource = _allItems;

        // Wire up search
        TxtSearch.TextChanged += (_, _) => ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = TxtSearch.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            LstConflicts.ItemsSource = _allItems;
        }
        else
        {
            LstConflicts.ItemsSource = _allItems
                .Where(x => x.FilePath.Contains(query, System.StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}

// UI Model for the ListBox
public class ConflictViewModel
{
    public string FilePath { get; set; } = "";
    public string Status { get; set; } = "";
    public IBrush StatusColor { get; set; } = Brushes.White;
    public string Details { get; set; } = "";
}