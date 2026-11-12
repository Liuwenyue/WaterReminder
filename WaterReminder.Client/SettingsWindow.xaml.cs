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

    public SettingsWindow(ClientSettingsResponse current, Func<SettingsUpdate, Task<string?>> save, DateTime now, int consumedMl)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _save = save;
        _now = now;
        _consumedMl = consumedMl;
        _days = [Mon, Tue, Wed, Thu, Fri, Sat, Sun];
        MaxHeight = SystemParameters.WorkArea.Height - 16;

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

        foreach (var stepper in new[] { GoalStepper, WorkCupStepper, HomeCupStepper, StartStepper, EndStepper, LunchStartStepper, LunchEndStepper, NoDrinkStepper, GapStepper, QuietStepper })
            stepper.ValueChanged += (_, _) => Validate();
        Validate();
        WindowEx.EnableDrag(this);
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
        ActiveDays = _days.Select(x => x.IsChecked == true).ToArray(),
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

        SaveButton.IsEnabled = validation.Ok;
        return validation.Ok;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        SaveButton.IsEnabled = false;
        SaveError.Text = "";
        try
        {
            var error = await _save(Draft());
            if (error is null)
            {
                DialogResult = true;
                return;
            }
            SaveError.Text = error;
        }
        catch
        {
            SaveError.Text = "保存失败";
        }
        finally
        {
            if (DialogResult != true) SaveButton.IsEnabled = true;
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
