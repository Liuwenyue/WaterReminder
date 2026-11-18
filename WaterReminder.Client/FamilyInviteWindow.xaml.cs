using System.Windows;
using WaterReminder.Client.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

public partial class FamilyInviteWindow : Window
{
    private readonly ReminderApiClient _api;

    public FamilyInviteWindow(ReminderApiClient api)
    {
        InitializeComponent();
        _api = api;
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        Loaded += (_, _) => MemberName.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (AccessCode.Visibility == Visibility.Visible)
        {
            AccessCode.Focus();
            AccessCode.SelectAll();
            try { Clipboard.SetText(AccessCode.Text); Feedback.Text = "访问码已复制。"; }
            catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = "请按 Ctrl+C 复制已选中的访问码。"; }
            return;
        }
        if (string.IsNullOrWhiteSpace(MemberName.Text))
        {
            MemberName.Tag = "error"; Feedback.Text = "请输入成员名称。";
            return;
        }

        MemberName.Tag = null; CreateButton.IsEnabled = false;
        MemberName.IsEnabled = false;
        PushToken.IsEnabled = false;
        Feedback.Text = "正在创建…";
        var response = await _api.AddFamilyMemberAsync(new(MemberName.Text, PushToken.Password));
        CreateButton.IsEnabled = true;
        if (!response.Ok || response.Value is not { } invitation)
        {
            MemberName.IsEnabled = true;
            PushToken.IsEnabled = true;
            MemberName.Tag = "error"; Feedback.Text = response.Error ?? "创建失败，请检查服务端连接。";
            return;
        }
        PushToken.Clear();
        AccessCode.Text = invitation.AccessCode;
        AccessCode.Visibility = Visibility.Visible;
        CreateButton.Content = "复制访问码";
        Feedback.Text = "请将访问码交给该成员。成员客户端连接同一服务端时，用这个访问码作为 API 密钥。关闭后无法再次查看。";
    }
}
