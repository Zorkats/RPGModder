using System;
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

        TxtVersion.Text = $"v{info.Version} is available";
        TxtReleaseNotes.Text = info.ReleaseNotes;
    }

    private async void BtnUpdate_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnSkip.IsEnabled = false;
        PrgDownload.IsVisible = true;
        TxtStatus.Text = "Downloading update...";

        var progress = new Progress<double>(p => PrgDownload.Value = p);

        try
        {
            await _service.DownloadAndInstallAsync(_info.DownloadUrl, progress);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Update failed.";
            await new MessageBox("Error", $"Update failed: {ex.Message}").ShowDialog(this);
            Close();
        }
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e) => Close();
}