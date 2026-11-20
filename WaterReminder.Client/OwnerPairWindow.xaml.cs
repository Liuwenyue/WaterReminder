using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

public partial class OwnerPairWindow : Window
{
    private readonly ReminderApiClient _api;
    private bool _binding;
    private bool _bound;

    public OwnerPairWindow(ReminderApiClient api)
    {
        _api = api;
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
        Closing += (_, e) => e.Cancel = _binding;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        if (_bound)
        {
            DialogResult = true;
            return;
        }
        var code = CodeBox.Text.Trim();
        if (code.Length != 8 || code.Any(c => !char.IsAsciiDigit(c)))
        {
            ShowFeedback("请输入手机上显示的 8 位数字。", "Danger");
            CodeBox.Focus();
            return;
        }
        _binding = true;
        ConfirmButton.IsEnabled = CancelButton.IsEnabled = CodeBox.IsEnabled = false;
        ConfirmButton.Content = "正在绑定…";
        ShowFeedback("正在确认配对…", "TextSecondary");
        var result = await _api.ConfirmOwnerPairAsync(code);
        _binding = false;
        _bound = result.Ok;
        ConfirmButton.IsEnabled = true;
        if (_bound)
        {
            ShowFeedback("绑定成功。回到手机点「继续」打开管理员账本。", "Success");
            ConfirmButton.Content = "完成";
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Focus();
            return;
        }
        ShowFeedback(result.Error ?? "无法确认配对。请检查是否使用管理员桌面端，并重试。", "Danger");
        ConfirmButton.Content = "确认绑定";
        CancelButton.IsEnabled = CodeBox.IsEnabled = true;
        CodeBox.Focus();
        CodeBox.SelectAll();
    }

    private void Code_Changed(object sender, TextChangedEventArgs e)
    {
        if (Feedback is null || _binding || _bound || !Equals(CodeBox.Tag, "error")) return;
        CodeBox.Tag = null;
        Feedback.Visibility = Visibility.Collapsed;
    }

    private void ShowFeedback(string text, string color)
    {
        CodeBox.Tag = color == "Danger" ? "error" : null;
        Feedback.SetResourceReference(TextBlock.ForegroundProperty, color);
        Feedback.Text = text;
        Feedback.Visibility = Visibility.Visible;
        UIElementAutomationPeer.FromElement(Feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
