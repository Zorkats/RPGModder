using System;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RPGModder.Core.Services;

namespace RPGModder.UI.Dialogs;

public partial class UpdateDialog : Window
{
    private readonly UpdateService _service;
    private readonly UpdateInfo _info;

    public UpdateDialog() { InitializeComponent(); }

    public UpdateDialog(UpdateService service, UpdateInfo info) : this()
    {
        _service = service;
        _info = info;

        TxtVersion.Text = $"v{info.Version} is available (you have v{service.CurrentVersion})";
        TxtReleaseNotes.Text = CleanReleaseNotes(info.ReleaseNotes);
    }

    // Strip heavy markdown formatting so it reads well as plain text
    private static string CleanReleaseNotes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "No release notes provided.";

        var text = raw;

        // Turn ### headings into plain bold-style lines
        text = Regex.Replace(text, @"^#{1,4}\s*", "", RegexOptions.Multiline);

        // Turn **bold** into plain text
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");

        // Turn `code` into plain text
        text = Regex.Replace(text, @"`([^`]+)`", "$1");

        // Clean up excessive blank lines
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    private async void BtnUpdate_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnSkip.IsEnabled = false;
        PrgDownload.IsVisible = true;
        TxtStatus.Text = "Downloading update...";

        var progress = new Progress<double>(p =>
        {
            PrgDownload.Value = p;
            TxtStatus.Text = $"Downloading... {p:F0}%";
        });

        try
        {
            await _service.DownloadAndInstallAsync(_info.DownloadUrl, progress);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Update failed.";
            BtnUpdate.IsEnabled = true;
            BtnSkip.IsEnabled = true;
            PrgDownload.IsVisible = false;
            await new MessageBox("Error", $"Update failed: {ex.Message}").ShowDialog(this);
        }
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e) => Close();
}
