using Avalonia.Controls;
using Avalonia.Interactivity;
using RPGModder.Core.Models;
using RPGModder.Core.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPGModder.UI.Dialogs;

public partial class ModLinkWindow : Window
{
    private readonly NexusApiService _nexus = null!;
    private readonly string _gameDomain = "";
    public NexusMod? SelectedMod { get; private set; }

    public ModLinkWindow() { InitializeComponent(); }

    public ModLinkWindow(NexusApiService nexus, string gameDomain, string initialSearch) : this()
    {
        _nexus = nexus;
        _gameDomain = gameDomain;
        TxtSearch.Text = initialSearch;

        BtnSearch.Click += async (_, _) => await SearchAsync();
        BtnLink.Click += (_, _) => { SelectedMod = LstResults.SelectedItem as NexusMod; Close(true); };
        BtnCancel.Click += (_, _) => Close(false);
        BtnManual.Click += async (_, _) => await ShowManualInputDialog();

        LstResults.SelectionChanged += (_, _) => BtnLink.IsEnabled = LstResults.SelectedItem != null;

        // Auto-search if we have a domain and initial name
        if (!string.IsNullOrEmpty(gameDomain) && !string.IsNullOrEmpty(initialSearch))
        {
            _ = SearchAsync();
        }
    }

    private async Task SearchAsync()
    {
        string query = TxtSearch.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query)) return;

        TxtStatus.Text = "Searching Nexus...";
        TxtStatus.IsVisible = true;
        LstResults.IsVisible = false;

        var result = await _nexus.SearchModsAsync(_gameDomain, query);

        if (result.Success && result.Mods.Count > 0)
        {
            LstResults.ItemsSource = result.Mods;
            TxtStatus.IsVisible = false;
            LstResults.IsVisible = true;
        }
        else
        {
            TxtStatus.Text = result.Success ? "No results found." : $"Search failed: {result.Error}";
            TxtStatus.IsVisible = true;
            LstResults.IsVisible = false;
        }
    }

    private async Task ShowManualInputDialog()
    {
        var dialog = new TextInputDialog("Manual Link", "Enter Nexus Mod ID:");
        var resultId = await dialog.ShowDialog<string?>(this);

        if (int.TryParse(resultId, out int id))
        {
            SelectedMod = new NexusMod { ModId = id, Name = "Manual Link" };
            Close(true);
        }
    }
}
