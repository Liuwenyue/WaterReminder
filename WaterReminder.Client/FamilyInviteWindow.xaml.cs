using System.Windows;
using WaterReminder;
using WaterReminder.Controls;
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
        DialogLayout.KeepOnScreen(this);
        Feedback.Text = "生成后请将访问码交给该成员。成员客户端连接同一服务端时，用这个访问码作为 API 密钥。";
        Loaded += (_, _) => MemberName.Focus();
    }

    /// <summary>Shows a freshly reset access code so the owner can copy it to the member.</summary>
    public FamilyInviteWindow(ReminderApiClient api, FamilyInvitation invitation)
    {
        InitializeComponent();
        _api = api;
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        DialogLayout.KeepOnScreen(this);
        Title = "重置访问码";
        HeaderText.Text = "重置访问码";
        MemberName.Text = invitation.Member.Name;
        MemberName.IsEnabled = false;
        PushToken.Visibility = Visibility.Collapsed;
        PushLabel.Visibility = Visibility.Collapsed;
        ShowAccessCode(invitation.AccessCode);
        Feedback.Text = "旧访问码已失效。请将新访问码交给该成员，成员客户端用它作为 API 密钥重新连接。关闭后无法再次查看。";
        Loaded += (_, _) => CreateButton.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (AccessCode.Visibility == Visibility.Visible)
        {
            AccessCode.Focus();
            AccessCode.SelectAll();
            try
            {
                Clipboard.SetText(AccessCode.Text);
                Feedback.Text = "已复制。";
            }
            catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = "请按 Ctrl+C 复制已选中的内容。"; }
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
        ShowAccessCode(invitation.AccessCode);
        Feedback.Text = "请将访问码交给该成员。成员客户端连接同一服务端时，用这个访问码作为 API 密钥。关闭后无法再次查看。";
    }

    private void ShowAccessCode(string code)
    {
        AccessCode.Text = code;
        AccessCode.Visibility = Visibility.Visible;
        CreateButton.Content = "复制访问码";
    }
}
