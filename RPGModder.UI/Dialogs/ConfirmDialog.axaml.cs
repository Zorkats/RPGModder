using Avalonia.Controls;

namespace RPGModder.UI.Dialogs;

/// <summary>
/// What the player picked in a <see cref="ConfirmDialog"/>. Closing the window counts as Cancel.
/// </summary>
public enum ConfirmChoice
{
    Cancel,
    Confirm,
    Alternate,
}

/// <summary>
/// Asks before an action that loses or changes something. Cancel is the default button and has the
/// focus, so Enter and Escape both keep things as they are.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string confirmLabel, string? alternateLabel = null, string? cancelLabel = null)
        : this()
    {
        TxtTitle.Text = title;
        if (cancelLabel != null)
        {
            BtnCancel.Content = cancelLabel;
        }

        TxtMessage.Text = message;
        BtnConfirm.Content = confirmLabel;
        BtnConfirm.Click += (_, _) => Close(ConfirmChoice.Confirm);
        BtnCancel.Click += (_, _) => Close(ConfirmChoice.Cancel);
        if (alternateLabel != null)
        {
            BtnAlternate.Content = alternateLabel;
            BtnAlternate.IsVisible = true;
            BtnAlternate.Click += (_, _) => Close(ConfirmChoice.Alternate);
        }

        Opened += (_, _) => BtnCancel.Focus();
    }

    public async System.Threading.Tasks.Task<ConfirmChoice> AskAsync(Window owner)
    {
        object? choice = await ShowDialog<object?>(owner);
        return choice is ConfirmChoice picked ? picked : ConfirmChoice.Cancel;
    }
}
