using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RPGModder.UI.Dialogs;

public partial class TextInputDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public TextInputDialog() { InitializeComponent(); }

    public TextInputDialog(string title, string prompt) : this()
    {
        TxtTitle.Text = title;
        TxtInput.Watermark = prompt;
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        ResultText = TxtInput.Text ?? "";
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}