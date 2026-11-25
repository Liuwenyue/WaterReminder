using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaterReminder;
using WaterReminder.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>
/// The original desktop settings window, wired to the server. It edits reminder values only.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ToggleButton[] _days;
    private readonly Func<SettingsUpdate, Task<string?>> _save;
    private readonly ReminderApiClient? _api;
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
        string? memberName = null,
        ReminderApiClient? api = null)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _save = save;
        _api = api;
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
        else if (api is not null)
        {
            // Own settings for a member: the PushWard field appears only after
            // GetFamilyAsync confirms the caller is not the owner (see Loaded).
            if (!current.PushWardConfigured)
            {
                ClearOwnPushTokenBox.Visibility = Visibility.Collapsed;
                OwnPushTokenHint.Text = "尚未保存密钥。输入 PushWard 密钥并保存后，手机推送即可使用该密钥。";
            }
            Loaded += RefreshOwnPushTokenVisibility;
        }
        Closed += (_, _) =>
        {
            MemberPushToken.Clear();
            OwnPushToken.Clear();
        };

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

    private void ClearOwnPushToken_Click(object sender, RoutedEventArgs e)
    {
        var clearing = ClearOwnPushTokenBox.IsChecked == true;
        if (clearing) OwnPushToken.Clear();
        OwnPushToken.IsEnabled = !clearing;
    }

    /// <summary>
    /// Shows the member's own PushWard field only when the caller is a regular
    /// member. The owner never sees it (the server also rejects the owner with
    /// 403). Fails closed: on any error the field simply stays hidden.
    /// </summary>
    private async void RefreshOwnPushTokenVisibility(object sender, RoutedEventArgs e)
    {
        Loaded -= RefreshOwnPushTokenVisibility;
        if (_api is null) return;
        try
        {
            var family = await _api.GetFamilyAsync();
            if (!IsVisible) return;
            if (family.Ok && family.Value is { IsOwner: false })
            {
                OwnPushTokenPanel.Visibility = Visibility.Visible;
                MobilePushHint.Text = "由服务器发送。密钥只留在服务器上，这里不显示原文。";
            }
        }
        catch
        {
            // Fail closed: without a confirmed member role the field stays hidden.
        }
    }

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
        // PhoneMode is not edited here: leaving it null keeps the stored value.
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
                error = await SaveOwnPushTokenIfChangedAsync();
            if (!IsVisible) return;
            if (error is null)
            {
                MemberPushToken.Clear();
                OwnPushToken.Clear();
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

    /// <summary>
    /// Calls PUT /api/settings/push-token only when the member actually changed
    /// their own credential: a typed value replaces it, the clear box removes
    /// it, and an untouched empty box sends nothing. Runs after the regular
    /// settings save succeeded, so a failure here keeps the dialog open with a
    /// Chinese message (the server's own text wins via FriendlyError). Returns
    /// null on success or when there was nothing to send. Never logs the token.
    /// </summary>
    private async Task<string?> SaveOwnPushTokenIfChangedAsync()
    {
        if (_api is null || OwnPushTokenPanel.Visibility != Visibility.Visible) return null;
        string? token;
        if (ClearOwnPushTokenBox.IsChecked == true) token = null;
        else if (!string.IsNullOrWhiteSpace(OwnPushToken.Password)) token = OwnPushToken.Password.Trim();
        else return null;
        var result = await _api.UpdateOwnPushTokenAsync(token);
        if (result.Ok) return null;
        return "设置已保存，但推送密钥未更新：" + (string.IsNullOrWhiteSpace(result.Error) ? "请重试。" : result.Error);
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
