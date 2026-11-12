using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WaterReminder.Controls;
using WaterReminder.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WaterReminder;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty DisplayMlProperty = DependencyProperty.Register(
        nameof(DisplayMl), typeof(double), typeof(MainWindow), new PropertyMetadata(0d, (d, _) => ((MainWindow)d).UpdateAmountText()));

    private readonly AppServices _services;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private int? _shownConsumed;
    private bool _wasDone;
    private bool _editing;

    public double DisplayMl { get => (double)GetValue(DisplayMlProperty); set => SetValue(DisplayMlProperty, value); }
    public bool IsDemo { get; init; }

    // Expanded stays 352. Compact is wider so "下次提醒 16:19 · 建议 370 ml" fits on one line.
    private const double ExpandedWindowWidth = 352;
    private const double CompactWindowWidth = 420;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Width = services.Settings.CompactMode ? CompactWindowWidth : ExpandedWindowWidth;
        Loaded += (_, _) => { ApplyMode(); PlaceWindow(); RefreshView(); };
        _refreshTimer.Tick += (_, _) => RefreshView();
        _refreshTimer.Start();
        _snackTimer.Tick += (_, _) => HideSnack();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_editing && e.OriginalSource is DependencyObject d && !IsInside(d, Editor)) EndEditTotal(true);
        };
        WindowEx.EnableDrag(this, SavePosition);
    }

    public void OpenSettings(bool firstRun = false)
    {
        var dialog = new SettingsWindow(_services, firstRun);
        if (IsVisible) dialog.Owner = this; else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dialog.ShowDialog() == true)
        {
            _services.Progress.SettingsChanged(); // goal/cup/hours changed -> recompute next reminder + tray
            RefreshView();
            if (!_services.Settings.FloatingWindow) Hide();
        }
    }

    // ---------- layout ----------

    private IReadOnlyList<ScreenRect> WorkAreas()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(x => x.Primary)
            .Select(x => new ScreenRect(x.WorkingArea.Left / dpi.DpiScaleX, x.WorkingArea.Top / dpi.DpiScaleY,
                                        x.WorkingArea.Width / dpi.DpiScaleX, x.WorkingArea.Height / dpi.DpiScaleY))
            .ToList();
    }

    private void PlaceWindow()
    {
        if (IsDemo) return;
        var compact = _services.Settings.CompactMode;
        (Left, Top) = WindowPlacement.Resolve(_services.Settings, compact, ActualWidth, ActualHeight, WorkAreas());
    }

    /// <summary>After a user drag: remember the spot for the current mode only (compact and expanded are kept apart).</summary>
    private void SavePosition()
    {
        if (IsDemo) return;
        WindowPlacement.Remember(_services.Settings, _services.Settings.CompactMode, Left, Top);
        _services.Storage.SaveSettings(_services.Settings);
    }

    public void SetCompact(bool compact)
    {
        _services.Settings.CompactMode = compact;
        ApplyMode();
    }

    private void ApplyMode()
    {
        var compact = _services.Settings.CompactMode;
        var before = new ScreenRect(Left, Top, ActualWidth, ActualHeight);
        ExpandedPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Width = compact ? CompactWindowWidth : ExpandedWindowWidth;
        if (!IsLoaded || IsDemo || double.IsNaN(Left) || double.IsNaN(Top)) return;
        UpdateLayout();
        // compact -> back to where the user put the compact window; expanded -> its own spot, or grow upward
        (Left, Top) = WindowPlacement.Resolve(_services.Settings, compact, ActualWidth, ActualHeight, WorkAreas(), before);
    }

    // ---------- view ----------

    public void RefreshView()
    {
        _services.RefreshToday();
        var now = DateTime.Now;
        var settings = _services.Settings;
        var record = _services.Today;
        var consumed = record.ConsumedMl;
        var goal = Math.Max(1, settings.DailyGoalMl);
        var progress = consumed / (double)goal;
        var done = consumed >= goal;
        var cup = WaterCalculator.CurrentCupMl(settings, now);

        DateText.Text = now.ToString("M月d日 dddd");
        if (!_editing) GoalText.Text = $"/ {settings.DailyGoalMl:N0} ml";
        var percent = (int)Math.Floor(Math.Min(100, progress * 100));
        PercentText.Text = done ? "目标达成" : $"{percent}%";
        MiniPercent.Text = $"{percent}%";
        MiniAmount.Text = $"{consumed:N0}";
        MiniGoal.Text = $" / {settings.DailyGoalMl:N0} ml";
        DrinkAmount.Text = $"  {cup} ml";

        if (_shownConsumed != consumed)
        {
            var animate = _shownConsumed is not null;
            Animate(Ring, ProgressRing.ProgressProperty, progress, animate);
            Animate(MiniRing, ProgressRing.ProgressProperty, progress, animate);
            Animate(this, DisplayMlProperty, consumed, animate);
            _shownConsumed = consumed;
        }

        Ring.SetResourceReference(ProgressRing.FillBrushProperty, done ? "RingDone" : "RingFill");
        MiniRing.SetResourceReference(ProgressRing.FillBrushProperty, done ? "RingDone" : "RingFill");
        PercentPill.SetResourceReference(BackgroundProperty, done ? "SuccessSoft" : "AccentSoft");
        PercentText.SetResourceReference(ForegroundProperty, done ? "Success" : "AccentText");
        if (done && !_wasDone && IsLoaded) Celebrate();
        _wasDone = done;

        DrinkButton.IsEnabled = !done;
        MiniDrink.IsEnabled = !done;
        DrinkLabel.Text = done ? "今日目标已完成" : "喝一杯";
        DrinkAmount.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        DrinkGlyph.Text = done ? "\uE73E" : "\uE710";
        MiniDrink.ToolTip = done ? "今日目标已完成" : $"喝一杯（{cup} ml）";

        var plan = _services.Progress.Plan();
        var phase = WaterCalculator.PhaseAt(settings, now);
        var status = WaterCalculator.StatusLine(settings, record, plan, now, compact: false);
        string glyph; string brush;
        if (done) { glyph = "\uE735"; brush = "Success"; }
        else if (settings.RemindersPaused) { glyph = "\uE769"; brush = "Warning"; }
        else if (!WaterCalculator.IsActiveDay(settings, now) || phase is WaterCalculator.DayPhase.NoDrinkWindow or WaterCalculator.DayPhase.Lunch || plan.NextReminder is null) { glyph = "\uE708"; brush = "TextSecondary"; }
        else if (plan.SuggestedMl > 0) { glyph = "\uEA8F"; brush = "AccentText"; }
        else { glyph = "\uE823"; brush = "TextSecondary"; }
        StatusText.Text = status;
        StatusGlyph.Text = glyph;
        StatusText.SetResourceReference(ForegroundProperty, brush);
        StatusGlyph.SetResourceReference(ForegroundProperty, brush);
        StatusPill.SetResourceReference(BackgroundProperty, brush switch { "Success" => "SuccessSoft", "Warning" => "WarningSoft", "AccentText" => "AccentSoft", _ => "SurfaceAlt" });
        if (!_editing) MiniStatus.Text = WaterCalculator.StatusLine(settings, record, plan, now, compact: true);

        PauseGlyph.Text = settings.RemindersPaused ? "\uE768" : "\uE769";
        PauseLabel.Text = settings.RemindersPaused ? "恢复" : "暂停";
        PauseButton.ToolTip = settings.RemindersPaused ? "恢复提醒" : "今天剩余时间不再提醒";
    }

    private void UpdateAmountText() => AmountText.Text = Math.Round(DisplayMl).ToString("N0");

    private static void Animate(DependencyObject target, DependencyProperty dp, double to, bool animate)
    {
        if (target is not IAnimatable a) return;
        if (!animate) { a.BeginAnimation(dp, null); target.SetValue(dp, to); return; }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        a.BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void Celebrate()
    {
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromPercent(0.4)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } });
        RingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        RingScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
    }

    private void ShowSnack(string text)
    {
        SnackText.Text = text;
        Snackbar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _snackTimer.Stop();
        _snackTimer.Start();
    }

    private void HideSnack()
    {
        _snackTimer.Stop();
        Snackbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>Demo helper: shows the snackbar statically for screenshots.</summary>
    public void DemoSnack(string text) { SnackText.Text = text; Snackbar.BeginAnimation(OpacityProperty, null); Snackbar.Opacity = 1; }

    // ---------- actions ----------

    private void AddWater(int amount, string source)
    {
        var applied = _services.Progress.Drink(amount, source); // saves + raises Changed -> RefreshView/tray
        if (applied == 0) return;
        RefreshView();
        if (CompactPanel.Visibility != Visibility.Visible)
            ShowSnack(applied > 0 ? $"已记录 +{applied} ml" : $"已减少 {-applied} ml");
    }

    private void DrinkButton_Click(object sender, RoutedEventArgs e)
        => AddWater(WaterCalculator.CurrentCupMl(_services.Settings, DateTime.Now), "cup");

    private void CustomButton_Click(object sender, RoutedEventArgs e)
    {
        var cup = WaterCalculator.CurrentCupMl(_services.Settings, DateTime.Now);
        var dialog = new AmountWindow(initial: cup, cup: cup) { Owner = this };
        if (dialog.ShowDialog() == true) AddWater(dialog.AmountMl, "custom");
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Settings.RemindersPaused)
        {
            _services.Settings.RemindersPaused = false;
            _services.Settings.PauseUntil = null;
        }
        else
        {
            // "暂停" pauses for the rest of today; reminders resume automatically tomorrow.
            _services.Settings.RemindersPaused = true;
            _services.Settings.PauseUntil = DateTime.Today.AddDays(1);
        }
        _services.Storage.SaveSettings(_services.Settings);
        _services.Progress.SettingsChanged();
        RefreshView();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        SetCompact(!_services.Settings.CompactMode);
        if (!IsDemo) _services.Storage.SaveSettings(_services.Settings);
    }

    private void RecordsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RecordsWindow(_services) { Owner = this };
        dialog.ShowDialog();
        RefreshView();
    }

    // ---------- inline correction of today's total ----------

    private void AmountText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // don't start a window drag
        BeginEditTotal();
    }

    private void MiniAmount_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginEditTotal();
    }

    private bool CompactActive => CompactPanel.Visibility == Visibility.Visible;
    private System.Windows.Controls.TextBox Editor => CompactActive ? MiniEditor : AmountEditor;
    private FrameworkElement EditorHost => CompactActive ? MiniAmountHit : AmountHit;
    private System.Windows.Controls.TextBlock HintText => CompactActive ? MiniStatus : GoalText;

    public void BeginEditTotal()
    {
        if (_editing) return;
        _services.RefreshToday();
        _editing = true;
        var editor = Editor;
        editor.Text = _services.Today.ConsumedMl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EditorHost.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        SetEditHint(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { editor.Focus(); Keyboard.Focus(editor); editor.SelectAll(); });
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur is not null; cur = cur is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    private void SetEditHint(string? error)
    {
        HintText.Text = error ?? "回车确认 · Esc 取消";
        if (error is null) HintText.ClearValue(ForegroundProperty); else HintText.SetResourceReference(ForegroundProperty, "Danger");
    }

    private void EndEditTotal(bool commit)
    {
        if (!_editing) return;
        if (commit)
        {
            if (!WaterCalculator.TryParseTotal(Editor.Text, out var total, out var error))
            {
                SetEditHint(error);
                Editor.Focus();
                Editor.SelectAll();
                return;
            }
            _editing = false;
            CloseEditor();
            ApplyCorrection(total);
        }
        else
        {
            _editing = false;
            CloseEditor();
        }
        RefreshView();
    }

    private void CloseEditor()
    {
        AmountEditor.Visibility = Visibility.Collapsed;
        MiniEditor.Visibility = Visibility.Collapsed;
        AmountHit.Visibility = Visibility.Visible;
        MiniAmountHit.Visibility = Visibility.Visible;
        GoalText.ClearValue(ForegroundProperty);
        MiniStatus.ClearValue(ForegroundProperty);
        GoalText.Text = $"/ {_services.Settings.DailyGoalMl:N0} ml";
        Keyboard.ClearFocus();
        Focus();
    }

    private void ApplyCorrection(int newTotal)
    {
        var delta = _services.Progress.Correct(newTotal); // saves + raises Changed -> RefreshView/tray
        if (delta == 0) return;
        RefreshView();
        ShowSnack($"已修正为 {newTotal:N0} ml（{(delta > 0 ? "+" : "−")}{Math.Abs(delta):N0}）");
    }

    private void AmountEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndEditTotal(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEditTotal(false); e.Handled = true; }
        else if (e.Key == Key.Space) e.Handled = true; // never a valid character here
    }

    private void AmountEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editing && !IsDemo) EndEditTotal(true);
    }

    private void AmountEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_editing) return;
        SetEditHint(WaterCalculator.TryParseTotal(Editor.Text, out _, out var error) ? null : error);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editing || e.OriginalSource is System.Windows.Controls.TextBox) return;
        if (e.Key is Key.Space or Key.Enter) { AddWater(WaterCalculator.CurrentCupMl(_services.Settings, DateTime.Now), "keyboard"); e.Handled = true; }
        else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
