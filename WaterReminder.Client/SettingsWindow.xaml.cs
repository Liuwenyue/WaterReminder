using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaterReminder;
using WaterReminder.Client.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>
/// The original desktop settings window, wired to the server. It edits reminder values only.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ToggleButton[] _days;
    private readonly Func<SettingsUpdate, Task<string?>> _save;
    private readonly DateTime _now;
    private readonly int _consumedMl;
    private readonly bool _remindersPaused;
    private readonly string? _revision;
    private readonly string? _pauseUntil;
    private bool _saving;

    public bool FloatingWindow => FloatingBox.IsChecked == true;
    public bool AutoStart => AutoStartBox.IsChecked == true;
    public string? PushWardToken => string.IsNullOrWhiteSpace(MemberPushToken.Password) ? null : MemberPushToken.Password.Trim();

    public SettingsWindow(
        ClientSettingsResponse current,
        bool floatingWindow,
        bool autoStart,
        Func<SettingsUpdate, Task<string?>> save,
        DateTime now,
        int consumedMl,
        string? memberName = null)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _save = save;
        _now = now;
        _consumedMl = consumedMl;
        _remindersPaused = current.RemindersPaused;
        _revision = current.Revision;
        _pauseUntil = current.PauseUntil;
        _days = [Mon, Tue, Wed, Thu, Fri, Sat, Sun];

        GoalStepper.Value = current.DailyGoalMl;
        WorkCupStepper.Value = current.WorkCupMl;
        HomeCupStepper.Value = current.HomeCupMl;
        StartStepper.Value = Minutes(current.WorkStart);
        EndStepper.Value = Minutes(current.WorkEnd);
        LunchBox.IsChecked = current.LunchEnabled;
        LunchStartStepper.Value = Minutes(current.LunchStart);
        LunchEndStepper.Value = Minutes(current.LunchEnd);
        NoDrinkStepper.Value = current.NoDrinkBeforeEndMinutes;
        GapStepper.Value = current.MinReminderGapMinutes;
        QuietStepper.Value = Minutes(current.QuietStart);
        for (var i = 0; i < _days.Length && i < current.ActiveDays.Length; i++)
            _days[i].IsChecked = current.ActiveDays[i];
        MobilePushBox.IsChecked = current.MobilePushEnabled;
        PhoneModeBox.SelectedIndex = (int)current.PhoneMode;
        FloatingBox.IsChecked = floatingWindow;
        AutoStartBox.IsChecked = autoStart;
        if (memberName is not null)
        {
            Title = $"{memberName} · 成员设置";
            SettingsTitle.Text = Title;
            SettingsSubtitle.Text = "保存在服务器上，只修改该成员的提醒";
            MobilePushHint.Text = "由服务器发送，只用于该成员。";
            LocalPreferencesHeader.Visibility = Visibility.Collapsed;
            LocalPreferencesCard.Visibility = Visibility.Collapsed;
            MemberPushTokenPanel.Visibility = Visibility.Visible;
        }
        Closed += (_, _) => MemberPushToken.Clear();

        foreach (var stepper in new[] { GoalStepper, WorkCupStepper, HomeCupStepper, StartStepper, EndStepper, LunchStartStepper, LunchEndStepper, NoDrinkStepper, GapStepper, QuietStepper })
            stepper.ValueChanged += (_, _) => Validate();
        Validate();
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        DialogLayout.KeepOnScreen(this);
    }

    private void Day_Click(object sender, RoutedEventArgs e) => Validate();
    private void Lunch_Click(object sender, RoutedEventArgs e) => Validate();
    private void MobilePush_Click(object sender, RoutedEventArgs e) => Validate();

    private SettingsUpdate Draft() => new()
    {
        DailyGoalMl = GoalStepper.Value,
        WorkCupMl = WorkCupStepper.Value,
        HomeCupMl = HomeCupStepper.Value,
        WorkStart = Clock(StartStepper.Value),
        WorkEnd = Clock(EndStepper.Value),
        LunchEnabled = LunchBox.IsChecked == true,
        LunchStart = Clock(LunchStartStepper.Value),
        LunchEnd = Clock(LunchEndStepper.Value),
        NoDrinkBeforeEndMinutes = NoDrinkStepper.Value,
        QuietStart = Clock(QuietStepper.Value),
        MinReminderGapMinutes = GapStepper.Value,
        MobilePushEnabled = MobilePushBox.IsChecked == true,
        PhoneMode = (PhoneDeliveryMode)PhoneModeBox.SelectedIndex,
        ActiveDays = _days.Select(x => x.IsChecked == true).ToArray(),
        RemindersPaused = _remindersPaused,
        PreservePause = true,
        ExpectedRevision = _revision,
        PauseUntil = _pauseUntil,
    };

    private bool Validate()
    {
        var validation = SettingsEditor.Validate(Draft());
        Show(GoalError, validation.GoalError);
        Show(TimeError, validation.TimeError);
        LunchGrid.Visibility = LunchBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Show(LunchError, validation.LunchError);
        Show(NoDrinkError, validation.NoDrinkError);
        Show(GapError, validation.GapError);
        Show(QuietError, validation.QuietError);
        Show(DaysError, validation.DaysError);

        if (validation.Ok && SettingsEditor.TryApply(new UserSettings(), Draft(), out _))
        {
            var draft = new UserSettings();
            SettingsEditor.TryApply(draft, Draft(), out _);
            var record = new DailyRecord
            {
                Date = _now.ToString("yyyy-MM-dd"),
                Events = _consumedMl > 0 ? [new WaterEvent { AmountMl = _consumedMl, At = _now }] : [],
            };
            PlanSummary.Text = PlanSummaryText.Describe(draft, record, _now);
        }
        else
        {
            PlanSummary.Text = "设置有误时无法计算提醒计划";
        }

        SaveButton.IsEnabled = validation.Ok && !_saving;
        return validation.Ok;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || !Validate()) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        SettingsContent.IsEnabled = false;
        Show(SaveError, null);
        try
        {
            var error = await _save(Draft());
            if (!IsVisible) return;
            if (error is null)
            {
                MemberPushToken.Clear();
                DialogResult = true;
                return;
            }
            Show(SaveError, error);
        }
        catch
        {
            if (IsVisible) Show(SaveError, "保存失败");
        }
        finally
        {
            _saving = false;
            SettingsContent.IsEnabled = true;
            if (IsVisible) Validate();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; } catch (InvalidOperationException) { Close(); }
    }

    private static void Show(TextBlock block, string? error)
    {
        block.Text = error ?? "";
        block.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static int Minutes(string? clock)
        => SettingsEditor.TryParseClock(clock, out var time) ? (int)time.TotalMinutes : 0;

    private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}
