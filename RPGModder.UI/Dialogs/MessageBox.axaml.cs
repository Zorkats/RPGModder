using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RPGModder.UI.Dialogs;

public partial class MessageBox : Window
{
    public MessageBox() { InitializeComponent(); }

    public MessageBox(string title, string message, bool showCancel = false) : this()
    {
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnOk.Click += (_, _) => Close(true);

        if (showCancel)
        {
            BtnCancel.IsVisible = true;
            BtnCancel.Click += (_, _) => Close(false);
        }
    }
}