using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using WaterReminder;
using WaterReminder.Client.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

public partial class FamilyInviteWindow : Window
{
    private readonly ReminderApiClient _api;
    private readonly FamilyMember? _existing;

    public FamilyInviteWindow(ReminderApiClient api, FamilyMember? existing = null)
    {
        InitializeComponent();
        _api = api;
        _existing = existing;
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        if (existing is not null)
        {
            Title = "微信邀请";
            MemberName.Text = existing.Name;
            MemberName.IsEnabled = false;
            PushToken.Visibility = Visibility.Collapsed;
            PushLabel.Visibility = Visibility.Collapsed;
            CreateButton.Content = "生成微信邀请";
            Feedback.Text = "填写手机能够访问的 https 地址。邀请 15 分钟内有效，家人确认后，这个成员原来的访问码会失效，饮水历史仍保留。";
        }
        else Feedback.Text = "填写 https 地址会生成 15 分钟微信邀请。留空则生成 Windows 客户端访问码。";
        Loaded += (_, _) => (existing is null ? MemberName : ServerUrl).Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (AccessCode.Visibility == Visibility.Visible)
        {
            AccessCode.Focus();
            AccessCode.SelectAll();
            try { Clipboard.SetText(AccessCode.Text); Feedback.Text = "已复制。"; }
            catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = "请按 Ctrl+C 复制已选中的访问码。"; }
            return;
        }
        if (_existing is null && string.IsNullOrWhiteSpace(MemberName.Text))
        {
            MemberName.Tag = "error"; Feedback.Text = "请输入成员名称。";
            return;
        }
        if (_existing is not null && string.IsNullOrWhiteSpace(ServerUrl.Text))
        {
            ServerUrl.Tag = "error"; Feedback.Text = "请输入手机可访问的 HTTPS 地址。";
            return;
        }

        MemberName.Tag = null; ServerUrl.Tag = null; CreateButton.IsEnabled = false;
        MemberName.IsEnabled = _existing is null;
        PushToken.IsEnabled = false;
        ServerUrl.IsEnabled = false;
        Feedback.Text = "正在创建…";
        if (!string.IsNullOrWhiteSpace(ServerUrl.Text))
        {
            var invite = await _api.CreateInvitationAsync(new(_existing is null ? MemberName.Text : null, _existing?.Id, ServerUrl.Text.Trim(), _existing is null ? PushToken.Password : null));
            CreateButton.IsEnabled = true;
            if (!invite.Ok || invite.Value is not { } created)
            {
                if (_existing is null) MemberName.IsEnabled = true;
                PushToken.IsEnabled = _existing is null;
                ServerUrl.IsEnabled = true;
                Feedback.Text = invite.Error ?? "创建失败，请检查服务端连接。";
                return;
            }
            ShowInvite(created);
            return;
        }
        var response = await _api.AddFamilyMemberAsync(new(MemberName.Text, PushToken.Password));
        CreateButton.IsEnabled = true;
        if (!response.Ok || response.Value is not { } invitation)
        {
            MemberName.IsEnabled = true;
            PushToken.IsEnabled = true;
            ServerUrl.IsEnabled = true;
            MemberName.Tag = "error"; Feedback.Text = response.Error ?? "创建失败，请检查服务端连接。";
            return;
        }
        PushToken.Clear();
        AccessCode.Text = invitation.AccessCode;
        AccessCode.Visibility = Visibility.Visible;
        CreateButton.Content = "复制访问码";
        Feedback.Text = "请将访问码交给该成员。成员客户端连接同一服务端时，用这个访问码作为 API 密钥。关闭后无法再次查看。";
    }

    private void ShowInvite(MemberInvite invite)
    {
        PushToken.Clear();
        AccessCode.Text = invite.InviteToken;
        AccessCode.Visibility = Visibility.Visible;
        CreateButton.Content = "复制邀请码";
        var payload = "waterreminder|" + invite.ServerUrl + "|" + invite.InviteToken;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var bytes = new PngByteQRCode(data).GetGraphic(8);
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            InviteQr.Source = image;
            InviteQr.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            InviteQr.Visibility = Visibility.Collapsed;
        }
        Feedback.Text = "请让家人打开微信小程序「设置」，扫描二维码或粘贴邀请码，并确认姓名「" + invite.Member.Name + "」。邀请 15 分钟内有效。确认后旧访问码失效，历史保留。";
    }
}
