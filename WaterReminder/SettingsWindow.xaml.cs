using System.Windows;
using System.Windows.Controls.Primitives;
using WaterReminder.Controls;
using WaterReminder.Services;

namespace WaterReminder;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly ToggleButton[] _days;

    public SettingsWindow(AppServices services, bool firstRun)
    {
        InitializeComponent();
        _services = services;
        _days = [Mon, Tue, Wed, Thu, Fri, Sat, Sun];
        MaxHeight = SystemParameters.WorkArea.Height - 16;

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
        PushWardTokenBox.Password = s.PushWardApiToken;

        foreach (var st in new[] { GoalStepper, WorkCupStepper, HomeCupStepper, StartStepper, EndStepper, LunchStartStepper, LunchEndStepper, NoDrinkStepper, QuietStepper }) st.ValueChanged += (_, _) => Validate();
        PushWardTokenBox.PasswordChanged += (_, _) => Validate();
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
        ActiveDays = _days.Select(x => x.IsChecked == true).ToArray(),
        MobilePushEnabled = MobilePushBox.IsChecked == true,
        PushWardApiToken = PushWardTokenBox.Password.Trim(),
    };

    /// <summary>PushWard 集成密钥以 hlk_ 开头；长度只做宽松检查，真实格式由服务端判定。</summary>
    private static bool LooksLikePushWardToken(string value)
        => value.StartsWith("hlk_", StringComparison.Ordinal)
           && value.Length is >= 8 and <= 256
           && value.All(c => !char.IsWhiteSpace(c));

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

        string? pushWardErr = null;
        var pushWardToken = PushWardTokenBox.Password.Trim();
        if (pushWardToken.Length > 0 && !LooksLikePushWardToken(pushWardToken))
            pushWardErr = "PushWard API Token 格式不正确（应以 hlk_ 开头）。";
        Show(PushWardError, pushWardErr); ok &= pushWardErr is null;

        var anyDay = _days.Any(x => x.IsChecked == true);
        DaysError.Visibility = anyDay ? Visibility.Collapsed : Visibility.Visible; ok &= anyDay;

        if (ok)
        {
            // Same dynamic calculation the reminder timer uses: no fixed timetable is implied.
            PlanSummary.Text = PlanSummaryText.Describe(Draft(), _services.Today, DateTime.Now, _services.Progress.LastReminderAt);
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
        s.PushWardApiToken = PushWardTokenBox.Password.Trim();
        s.FirstRun = false;
        _services.Storage.SaveSettings(s);
        StartupService.SetEnabled(s.AutoStart);
        // Pick up enabled/disabled or token changes; stops the poller when push is off/unconfigured.
        _services.PushWard.Restart();
        DialogResult = true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var token = PushWardTokenBox.Password.Trim();

        TestButton.IsEnabled = false;
        TestStatus.Text = "发送中…";
        try
        {
            if (token.Length == 0)
            {
                TestStatus.Text = "请先填写 PushWard API Token（hlk_ 开头）。";
                return;
            }
            if (!LooksLikePushWardToken(token))
            {
                TestStatus.Text = "PushWard API Token 格式不正确（应以 hlk_ 开头）。";
                return;
            }

            // Test the value currently in the form, not the last saved settings.
            var sent = await _services.PushWard.PublishAsync(
                PushWardMessageBuilder.BuildTest(new UserSettings
                {
                    PushWardApiToken = token,
                    DailyGoalMl = GoalStepper.Value,
                    HomeCupCapacityMl = HomeCupStepper.Value,
                }, _services.Today.ConsumedMl));
            TestStatus.Text = sent
                ? "测试通知已发送。长按/下拉通知后点「喝水了」；首次填写 Token 时请先保存设置，轮询才会启动。"
                : "发送失败，请检查 PushWard API Token 和网络。";
        }
        catch
        {
            TestStatus.Text = "发送失败，请检查网络和推送设置。";
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
