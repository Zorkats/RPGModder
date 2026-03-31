using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RPGModder.UI.Dialogs;

public partial class TextInputDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public TextInputDialog() { InitializeComponent(); }

    public TextInputDialog(string title, string prompt, string defaultValue = "") : this()
    {
        TxtTitle.Text = title;
        TxtInput.Watermark = prompt;
        TxtInput.Text = defaultValue;
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Close(TxtInput.Text ?? "");
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}