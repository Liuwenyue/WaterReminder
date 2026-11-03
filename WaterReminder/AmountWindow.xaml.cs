using System.Windows;
using System.Windows.Controls;
using WaterReminder.Controls;

namespace WaterReminder;

public partial class AmountWindow : Window
{
    public int AmountMl { get; private set; }

    public AmountWindow(int initial = 200, int cup = 300, string? title = null, string okText = "记录")
    {
        InitializeComponent();
        if (title is not null) { TitleText.Text = title; Title = title; }
        OkButton.Content = okText;
        AmountStepper.Value = Math.Clamp(Math.Abs(initial), 1, 3000);
        AmountStepper.ValueChanged += (_, _) => ErrorText.Visibility = AmountStepper.IsValid ? Visibility.Collapsed : Visibility.Visible;

        foreach (var v in new[] { cup, 100, 250, 500 }.Distinct())
        {
            var b = new Button { Content = v == cup ? $"一杯 {v}" : $"{v}", Margin = new Thickness(0, 0, 8, 0), Tag = "16", Padding = new Thickness(4, 0, 4, 0), ToolTip = $"{v} ml" };
            b.SetResourceReference(StyleProperty, "ChipButton");
            var value = v;
            b.Click += (_, _) => AmountStepper.Value = value;
            QuickPanel.Children.Add(b);
        }
        WindowEx.EnableDrag(this);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!AmountStepper.IsValid) { ErrorText.Visibility = Visibility.Visible; return; }
        AmountMl = AmountStepper.Value;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
