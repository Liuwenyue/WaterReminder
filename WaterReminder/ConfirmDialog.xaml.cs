using System.Windows;
using WaterReminder.Controls;

namespace WaterReminder;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText, bool danger)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = confirmText;
        if (danger)
        {
            OkButton.SetResourceReference(StyleProperty, "DangerButton");
            IconBg.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DangerSoft");
            IconGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Danger");
            IconGlyph.Text = "\uE74D";
        }
        WindowEx.EnableDrag(this);
    }

    public static bool Ask(Window? owner, string title, string message, string confirmText, bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, danger);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner; else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
