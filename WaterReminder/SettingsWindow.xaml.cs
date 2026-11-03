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

        if (firstRun)
        {
            IntroText.Text = "欢迎使用喝水提醒";
            SubText.Text = "先设定你的目标，之后随时可以在这里修改";
            SaveButton.Content = "开始使用";
            CancelButton.Content = "稍后设置";
        }

        var s = services.Settings;
        GoalStepper.Value = s.DailyGoalMl;
        CupStepper.Value = s.CupCapacityMl;
        StartStepper.Value = (int)s.WorkStart.TotalMinutes;
        EndStepper.Value = (int)s.WorkEnd.TotalMinutes;
        LunchBox.IsChecked = s.LunchEnabled;
        LunchStartStepper.Value = (int)s.LunchStart.TotalMinutes;
        LunchEndStepper.Value = (int)s.LunchEnd.TotalMinutes;
        NoDrinkStepper.Value = s.NoDrinkBeforeEndMinutes;
        for (var i = 0; i < _days.Length; i++) _days[i].IsChecked = s.ActiveDays[i];
        SoundBox.IsChecked = s.SoundEnabled;
        AutoStartBox.IsChecked = s.AutoStart;
        FloatingBox.IsChecked = s.FloatingWindow;

        foreach (var st in new[] { GoalStepper, CupStepper, StartStepper, EndStepper, LunchStartStepper, LunchEndStepper, NoDrinkStepper }) st.ValueChanged += (_, _) => Validate();
        Validate();
        WindowEx.EnableDrag(this);
    }

    private void Day_Click(object sender, RoutedEventArgs e) => Validate();
    private void Lunch_Click(object sender, RoutedEventArgs e) => Validate();

    private UserSettings Draft() => new()
    {
        DailyGoalMl = GoalStepper.Value, CupCapacityMl = CupStepper.Value,
        WorkStart = TimeSpan.FromMinutes(StartStepper.Value), WorkEnd = TimeSpan.FromMinutes(EndStepper.Value),
        LunchEnabled = LunchBox.IsChecked == true,
        LunchStart = TimeSpan.FromMinutes(LunchStartStepper.Value), LunchEnd = TimeSpan.FromMinutes(LunchEndStepper.Value),
        NoDrinkBeforeEndMinutes = NoDrinkStepper.Value,
    };

    private bool Validate()
    {
        var ok = true;
        string? goalErr = null;
        if (!GoalStepper.IsValid) goalErr = "每日目标需为 100–10000 ml 的整数。";
        else if (!CupStepper.IsValid) goalErr = "每杯容量需为 50–3000 ml 的整数。";
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

        var anyDay = _days.Any(x => x.IsChecked == true);
        DaysError.Visibility = anyDay ? Visibility.Collapsed : Visibility.Visible; ok &= anyDay;

        if (ok || (goalErr is null && timeErr is null && lunchErr is null && noDrinkErr is null))
        {
            var slots = WaterCalculator.Schedule(Draft(), DateTime.Today);
            PlanSummary.Text = $"每天提醒 {slots.Count} 次：" + string.Join("、", slots.Select(x => x.At.ToString("HH:mm")));
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
        s.CupCapacityMl = CupStepper.Value;
        s.WorkStart = TimeSpan.FromMinutes(StartStepper.Value);
        s.WorkEnd = TimeSpan.FromMinutes(EndStepper.Value);
        s.LunchEnabled = LunchBox.IsChecked == true;
        s.LunchStart = TimeSpan.FromMinutes(LunchStartStepper.Value);
        s.LunchEnd = TimeSpan.FromMinutes(LunchEndStepper.Value);
        s.NoDrinkBeforeEndMinutes = NoDrinkStepper.Value;
        s.ActiveDays = _days.Select(x => x.IsChecked == true).ToArray();
        s.SoundEnabled = SoundBox.IsChecked == true;
        s.AutoStart = AutoStartBox.IsChecked == true;
        s.FloatingWindow = FloatingBox.IsChecked == true;
        s.FirstRun = false;
        _services.Storage.SaveSettings(s);
        StartupService.SetEnabled(s.AutoStart);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // first run: keep defaults; the wizard shows again next launch
        try { DialogResult = false; } catch (InvalidOperationException) { Close(); }
    }
}
