using System.Windows;
using System.Windows.Controls.Primitives;
using WaterReminder.Controls;
using WaterReminder.Services;

namespace WaterReminder;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _firstRun;
    private readonly ToggleButton[] _days;

    public SettingsWindow(AppServices services, bool firstRun)
    {
        InitializeComponent();
        _services = services;
        _firstRun = firstRun;
        _days = [Mon, Tue, Wed, Thu, Fri, Sat, Sun];
        MaxHeight = SystemParameters.WorkArea.Height - 16;
        services.EnsureNtfyTopics(); // make the topic visible/copyable, even before the user saves

        if (firstRun)
        {
            IntroText.Text = "欢迎使用喝水提醒";
            SubText.Text = "先设定你的目标，之后随时可以在这里修改";
            SaveButton.Content = "开始使用";
            CancelButton.Content = "稍后设置";
        }

        var s = services.Settings;
        GoalStepper.Value = s.DailyGoalMl;
        WorkCupStepper.Value = WaterCalculator.WorkCupMl(s);
        HomeCupStepper.Value = WaterCalculator.HomeCupMl(s);
        StartStepper.Value = (int)s.WorkStart.TotalMinutes;
        EndStepper.Value = (int)s.WorkEnd.TotalMinutes;
        LunchBox.IsChecked = s.LunchEnabled;
        LunchStartStepper.Value = (int)s.LunchStart.TotalMinutes;
        LunchEndStepper.Value = (int)s.LunchEnd.TotalMinutes;
        NoDrinkStepper.Value = s.NoDrinkBeforeEndMinutes;
        QuietStepper.Value = (int)s.QuietStart.TotalMinutes;
        for (var i = 0; i < _days.Length; i++) _days[i].IsChecked = s.ActiveDays[i];
        SoundBox.IsChecked = s.SoundEnabled;
        AutoStartBox.IsChecked = s.AutoStart;
        FloatingBox.IsChecked = s.FloatingWindow;
        MobilePushBox.IsChecked = s.MobilePushEnabled;
        ServerBox.Text = NtfyMessageBuilder.NormalizeServer(s.NtfyServer);
        TopicBox.Text = s.NtfyTopic;
        ActionTopicBox.Text = s.NtfyActionTopic;
        TokenBox.Text = s.NtfyToken;

        foreach (var st in new[] { GoalStepper, WorkCupStepper, HomeCupStepper, StartStepper, EndStepper, LunchStartStepper, LunchEndStepper, NoDrinkStepper, QuietStepper }) st.ValueChanged += (_, _) => Validate();
        Validate();
        WindowEx.EnableDrag(this);
    }

    private void Day_Click(object sender, RoutedEventArgs e) => Validate();
    private void Lunch_Click(object sender, RoutedEventArgs e) => Validate();
    private void MobilePush_Click(object sender, RoutedEventArgs e) => Validate();

    private UserSettings Draft() => new()
    {
        DailyGoalMl = GoalStepper.Value,
        CupCapacityMl = WorkCupStepper.Value, WorkCupCapacityMl = WorkCupStepper.Value, HomeCupCapacityMl = HomeCupStepper.Value,
        WorkStart = TimeSpan.FromMinutes(StartStepper.Value), WorkEnd = TimeSpan.FromMinutes(EndStepper.Value),
        LunchEnabled = LunchBox.IsChecked == true,
        LunchStart = TimeSpan.FromMinutes(LunchStartStepper.Value), LunchEnd = TimeSpan.FromMinutes(LunchEndStepper.Value),
        NoDrinkBeforeEndMinutes = NoDrinkStepper.Value,
        QuietStart = TimeSpan.FromMinutes(QuietStepper.Value),
    };

    private bool Validate()
    {
        var ok = true;
        string? goalErr = null;
        if (!GoalStepper.IsValid) goalErr = "每日目标需为 100–10000 ml 的整数。";
        else if (!WorkCupStepper.IsValid || !HomeCupStepper.IsValid) goalErr = "工作杯和家庭杯容量需为 50–3000 ml 的整数。";
        Show(GoalError, goalErr); ok &= goalErr is null;

        string? timeErr = null;
        if (!StartStepper.IsValid || !EndStepper.IsValid) timeErr = "请输入有效时间，例如 09:00。";
        else if (EndStepper.Value <= StartStepper.Value) timeErr = "下班时间必须晚于上班时间。";
        Show(TimeError, timeErr); ok &= timeErr is null;

        var lunchOn = LunchBox.IsChecked == true;
        LunchGrid.Visibility = lunchOn ? Visibility.Visible : Visibility.Collapsed;
        string? lunchErr = null;
        if (lunchOn && timeErr is null)
        {
            if (!LunchStartStepper.IsValid || !LunchEndStepper.IsValid) lunchErr = "请输入有效的午休时间。";
            else if (LunchEndStepper.Value <= LunchStartStepper.Value) lunchErr = "午休结束必须晚于午休开始。";
            else if (LunchStartStepper.Value < StartStepper.Value || LunchEndStepper.Value > EndStepper.Value) lunchErr = "午休需在上下班时间之内。";
        }
        Show(LunchError, lunchErr); ok &= lunchErr is null;

        string? noDrinkErr = null;
        if (!NoDrinkStepper.IsValid) noDrinkErr = "请输入 0–240 分钟。";
        else if (timeErr is null && NoDrinkStepper.Value >= EndStepper.Value - StartStepper.Value) noDrinkErr = "不喝水时段必须短于上班时长。";
        else if (timeErr is null && lunchErr is null && WaterCalculator.Schedule(Draft(), DateTime.Today).Count == 0) noDrinkErr = "去掉午休和下班前时段后没有可喝水的时间。";
        Show(NoDrinkError, noDrinkErr); ok &= noDrinkErr is null;

        string? quietErr = null;
        if (!QuietStepper.IsValid) quietErr = "请输入有效的免打扰开始时间。";
        else if (timeErr is null && QuietStepper.Value <= EndStepper.Value) quietErr = "免打扰开始必须晚于下班时间。";
        Show(QuietError, quietErr); ok &= quietErr is null;

        string? ntfyErr = null;
        if (!Uri.TryCreate(NtfyMessageBuilder.NormalizeServer(ServerBox.Text), UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            ntfyErr = "服务器需为 http(s) 地址。";
        else if (!string.IsNullOrWhiteSpace(TopicBox.Text) && !NtfyTopics.IsValid(TopicBox.Text.Trim()))
            ntfyErr = "订阅话题只能包含字母、数字、- 和 _（最多 64 字符）。";
        else if (!string.IsNullOrWhiteSpace(ActionTopicBox.Text) && !NtfyTopics.IsValid(ActionTopicBox.Text.Trim()))
            ntfyErr = "按钮回传话题只能包含字母、数字、- 和 _（最多 64 字符）。";
        Show(NtfyError, ntfyErr); ok &= ntfyErr is null;

        var anyDay = _days.Any(x => x.IsChecked == true);
        DaysError.Visibility = anyDay ? Visibility.Collapsed : Visibility.Visible; ok &= anyDay;

        if (ok || (goalErr is null && timeErr is null && lunchErr is null && noDrinkErr is null && quietErr is null && ntfyErr is null))
        {
            var draft = Draft();
            var slots = WaterCalculator.Schedule(draft, DateTime.Today);
            var text = $"每天工作时段提醒 {slots.Count} 次：" + string.Join("、", slots.Select(x => x.At.ToString("HH:mm")));
            if (draft.DailyGoalMl > 0 && HomeCupStepper.Value > 0)
                text += $"；下班后若未达标，按家庭杯 {HomeCupStepper.Value} ml 用手机提醒";
            PlanSummary.Text = text;
        }
        else PlanSummary.Text = "设置有误时无法计算提醒计划";

        SaveButton.IsEnabled = ok;
        return ok;
    }

    private static void Show(System.Windows.Controls.TextBlock block, string? error)
    {
        block.Text = error ?? "";
        block.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        var s = _services.Settings;
        s.DailyGoalMl = GoalStepper.Value;
        s.WorkCupCapacityMl = WorkCupStepper.Value;
        s.HomeCupCapacityMl = HomeCupStepper.Value;
        s.CupCapacityMl = WorkCupStepper.Value; // keep the legacy field in sync
        s.WorkStart = TimeSpan.FromMinutes(StartStepper.Value);
        s.WorkEnd = TimeSpan.FromMinutes(EndStepper.Value);
        s.LunchEnabled = LunchBox.IsChecked == true;
        s.LunchStart = TimeSpan.FromMinutes(LunchStartStepper.Value);
        s.LunchEnd = TimeSpan.FromMinutes(LunchEndStepper.Value);
        s.NoDrinkBeforeEndMinutes = NoDrinkStepper.Value;
        s.QuietStart = TimeSpan.FromMinutes(QuietStepper.Value);
        s.ActiveDays = _days.Select(x => x.IsChecked == true).ToArray();
        s.SoundEnabled = SoundBox.IsChecked == true;
        s.AutoStart = AutoStartBox.IsChecked == true;
        s.FloatingWindow = FloatingBox.IsChecked == true;
        s.MobilePushEnabled = MobilePushBox.IsChecked == true;
        s.NtfyServer = NtfyMessageBuilder.NormalizeServer(ServerBox.Text);
        s.NtfyTopic = TopicBox.Text.Trim();
        s.NtfyActionTopic = ActionTopicBox.Text.Trim();
        s.NtfyToken = TokenBox.Text.Trim();
        if (!NtfyTopics.IsValid(s.NtfyTopic)) s.NtfyTopic = NtfyTopics.Generate(NtfyTopics.MainPrefix);
        if (!NtfyTopics.IsValid(s.NtfyActionTopic)) s.NtfyActionTopic = NtfyTopics.Generate(NtfyTopics.ActionPrefix);
        s.FirstRun = false;
        _services.Storage.SaveSettings(s);
        StartupService.SetEnabled(s.AutoStart);
        _services.Ntfy.Restart(); // pick up enabled/disabled, server, topic or token changes
        DialogResult = true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var server = NtfyMessageBuilder.NormalizeServer(ServerBox.Text);
        var topic = TopicBox.Text.Trim();
        var actionTopic = ActionTopicBox.Text.Trim();
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !NtfyTopics.IsValid(topic) || !NtfyTopics.IsValid(actionTopic))
        {
            TestStatus.Text = "请先填写有效的服务器和话题。";
            return;
        }

        TestButton.IsEnabled = false;
        TestStatus.Text = "发送中…";
        try
        {
            var message = NtfyMessageBuilder.BuildTest(server, topic, actionTopic, TokenBox.Text);
            var sent = await _services.Ntfy.PublishAsync(message);
            TestStatus.Text = sent ? "测试通知已发送，请在手机上查看。" : "发送失败，请检查服务器、话题和网络。";
        }
        catch
        {
            TestStatus.Text = "发送失败，请检查服务器、话题和网络。";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // first run: keep defaults; the wizard shows again next launch
        try { DialogResult = false; } catch (InvalidOperationException) { Close(); }
    }
}
