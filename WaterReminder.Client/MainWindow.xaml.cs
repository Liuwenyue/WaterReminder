using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WaterReminder.Client.Controls;
using WaterReminder.Services;
using FormsScreen = System.Windows.Forms.Screen;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace WaterReminder.Client;

/// <summary>
/// The original desktop floating widget. It shows server status and logs drinks; it does not schedule reminders.
/// </summary>
internal partial class MainWindow : Window
{
    internal static readonly DependencyProperty DisplayMlProperty = DependencyProperty.Register(
        nameof(DisplayMl), typeof(double), typeof(MainWindow), new PropertyMetadata(0d, (d, _) => ((MainWindow)d).UpdateAmountText()));

    private readonly ReminderApiClient _api;
    private readonly ClientConfig _config;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private StatusResponse? _status;
    private int? _shownConsumed;
    private bool _wasDone;
    private bool _editing;
    private bool _allowClose;
    private bool _modeTransitioning;
    private int _modeGeneration;
    private int _steadyPolls;
    private string? _statusFingerprint;
    private bool _drinkRequestInFlight;
    private bool _pauseRequestInFlight;


    internal double DisplayMl { get => (double)GetValue(DisplayMlProperty); set => SetValue(DisplayMlProperty, value); }

    internal MainWindow(ReminderApiClient api, ClientConfig config, Action openSettings)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _api = api;
        _config = config;
        _openSettings = openSettings;
        Loaded += (_, _) => { ApplyMode(); PlaceWindow(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _refreshTimer.Stop();
                return;
            }

            _steadyPolls = 0;
            _refreshTimer.Interval = TimeSpan.FromSeconds(5);
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            _ = RefreshAsync();
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(fromTimer: true);
        _snackTimer.Tick += (_, _) => HideSnack();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_modeTransitioning) { e.Handled = true; return; }
            if (_editing && e.OriginalSource is DependencyObject source && !IsInside(source, Editor)) EndEditTotal(true);
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
        WindowEx.EnableDrag(this, SavePosition, WorkAreas, () => !_modeTransitioning);
        WpfInterop.EnableKeyboard(this);
    }

    internal new void Hide()
    {
        FinishModeTransition();
        base.Hide();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private IReadOnlyList<ScreenRect> WorkAreas()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        // Primary first so a missing saved monitor falls back to the main screen.
        return FormsScreen.AllScreens
            .OrderByDescending(x => x.Primary)
            .Select(x => new ScreenRect(
                x.WorkingArea.Left / dpi.DpiScaleX,
                x.WorkingArea.Top / dpi.DpiScaleY,
                x.WorkingArea.Width / dpi.DpiScaleX,
                x.WorkingArea.Height / dpi.DpiScaleY))
            .ToList();
    }

    private void PlaceWindow()
    {
        var settings = _config.Display().ToSettings();
        (Left, Top) = WindowPlacement.Place(settings, _config.CompactMode, ActualWidth, ActualHeight, WorkAreas());
    }

    private void SavePosition()
    {
        if (_modeTransitioning) return;
        var settings = _config.Display().ToSettings();
        // Only an intentional compact drag chooses a new compact anchor. An expanded
        // drag is temporary and must not replace the spot we return to on collapse.
        if (!_config.CompactMode && WindowPlacement.Saved(settings) is not null) return;
        // Called after an intentional drag, or immediately before leaving compact mode.
        WindowPlacement.Remember(settings, Left, Top);
        var display = _config.Display();
        display.Capture(settings);
        _config.ApplyDisplay(display);
        _config.Save();
    }

    private void ApplyMode(bool animate = false)
    {
        var compact = _config.CompactMode;
        var incoming = compact ? CompactPanel : ExpandedPanel;
        var outgoing = compact ? ExpandedPanel : CompactPanel;
        var from = (Left, Top);
        var oldHeight = ActualHeight;
        var width = ActualWidth;
        var canAnimate = animate && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation
            && oldHeight > 0 && double.IsFinite(from.Left) && double.IsFinite(from.Top);
        var generation = ++_modeGeneration;
        _modeTransitioning = true;

        (double Left, double Top) Target(double height) =>
            WindowPlacement.Resolve(_config.Display().ToSettings(), width, height, WorkAreas(),
                new ScreenRect(from.Left, from.Top, width, oldHeight));

        void Settle()
        {
            if (generation != _modeGeneration) return;
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = 1;
            incoming.Height = double.NaN;
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;
            UpdateLayout();
            if (IsLoaded && double.IsFinite(from.Left) && double.IsFinite(from.Top))
                (Left, Top) = Target(ActualHeight);
            _modeTransitioning = false;
        }

        void ShowTarget()
        {
            if (generation != _modeGeneration) return;
            if (canAnimate)
            {
                SizeToContent = SizeToContent.Manual;
                Height = oldHeight;
            }
            outgoing.BeginAnimation(OpacityProperty, null);
            outgoing.Opacity = 1;
            outgoing.Visibility = Visibility.Collapsed;
            incoming.Visibility = Visibility.Visible;
            incoming.Height = double.NaN;
            // Measure the entire card, including its border and shadow margin.
            Card.Measure(new System.Windows.Size(Math.Max(1, width), double.PositiveInfinity));
            var targetHeight = Card.DesiredSize.Height;
            if (!canAnimate) { Settle(); return; }

            var target = Target(targetHeight);
            incoming.Height = Math.Max(0, incoming.DesiredSize.Height - incoming.Margin.Top - incoming.Margin.Bottom);
            Height = targetHeight;
            Left = target.Left;
            Top = target.Top;
            var duration = TimeSpan.FromMilliseconds(260);
            DoubleAnimation Transition(double start, double end) => new(start, end, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            BeginAnimation(LeftProperty, Transition(from.Left, target.Left));
            BeginAnimation(TopProperty, Transition(from.Top, target.Top));
            incoming.BeginAnimation(OpacityProperty, Transition(0, 1));
            var resize = Transition(oldHeight, targetHeight);
            resize.Completed += (_, _) => Settle();
            BeginAnimation(HeightProperty, resize);
        }

        if (!canAnimate) { ShowTarget(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => ShowTarget();
        outgoing.BeginAnimation(OpacityProperty, fade);
    }

    private void FinishModeTransition()
    {
        if (!_modeTransitioning) return;
        ++_modeGeneration;
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        foreach (var panel in new[] { CompactPanel, ExpandedPanel })
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 1;
            panel.Height = double.NaN;
        }
        Height = double.NaN;
        SizeToContent = SizeToContent.Height;
        ApplyMode();
    }

    private async Task RefreshAsync(bool fromTimer = false)
    {
        if (!fromTimer)
        {
            _steadyPolls = 0;
            _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        }
        if (_editing) return;
        try
        {
            var result = await _api.GetStatusAsync();
            if (_editing) return;
            if (!result.Ok || result.Value is null)
            {
                if (result.StatusCode == 429)
                    StatusText.Text = "请求太频繁，稍后再试";
                NextInterval(unchanged: true);
                return;
            }

            var status = result.Value;
            var fingerprint = string.Join("|", status.ConsumedMl, status.GoalMl, status.RemindersPaused, status.NextReminder, status.StatusText, status.DesktopAlert?.Id, status.SuggestedMl);
            var unchanged = fromTimer && fingerprint == _statusFingerprint;
            _statusFingerprint = fingerprint;
            NextInterval(unchanged);
            ApplyStatus(status);
        }
        catch
        {
            // The tray already reports connection trouble. Keep the last numbers on screen.
            NextInterval(unchanged: true);
        }
    }

    private void NextInterval(bool unchanged)
    {
        if (unchanged) _steadyPolls++;
        else _steadyPolls = 0;
        _refreshTimer.Interval = _steadyPolls >= 2 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);
    }

    private void ApplyStatus(StatusResponse status)
    {
        _status = status;
        var consumed = status.ConsumedMl;
        var goal = Math.Max(1, status.GoalMl);
        var progress = consumed / (double)goal;
        var done = status.GoalMl > 0 && consumed >= status.GoalMl;
        var cup = FloatingWidgetView.CurrentCupMl(status);
        var percent = FloatingWidgetView.Percent(consumed, status.GoalMl);

        if (DateTime.TryParse(status.Now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var now))
            DateText.Text = now.ToString("M月d日 dddd");
        if (!_editing) GoalText.Text = $"/ {status.GoalMl:N0} ml";
        PercentText.Text = done ? "目标达成" : $"{percent}%";
        MiniPercent.Text = $"{percent}%";
        MiniAmount.Text = $"{consumed:N0}";
        MiniGoal.Text = $" / {status.GoalMl:N0} ml";
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

        var canDrink = FloatingWidgetView.CanDrink(consumed, status.GoalMl);
        DrinkButton.IsEnabled = canDrink && !_drinkRequestInFlight;
        MiniDrink.IsEnabled = canDrink && !_drinkRequestInFlight;
        DrinkLabel.Text = done ? "今日目标已完成" : "喝一杯";
        DrinkAmount.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        DrinkGlyph.Text = done ? "\uE73E" : "\uE710";
        MiniDrink.ToolTip = done ? "今日目标已完成" : $"喝一杯（{cup} ml）";

        StatusText.Text = status.StatusText;
        var brush = done ? "Success" : status.RemindersPaused ? "Warning" : status.SuggestedMl > 0 ? "AccentText" : "TextSecondary";
        StatusGlyph.Text = done ? "\uE735" : status.RemindersPaused ? "\uE769" : status.SuggestedMl > 0 ? "\uEA8F" : "\uE823";
        StatusText.SetResourceReference(ForegroundProperty, brush);
        StatusGlyph.SetResourceReference(ForegroundProperty, brush);
        StatusPill.SetResourceReference(BackgroundProperty, brush switch { "Success" => "SuccessSoft", "Warning" => "WarningSoft", "AccentText" => "AccentSoft", _ => "SurfaceAlt" });
        if (!_editing)
        {
            var compactLine = string.IsNullOrWhiteSpace(status.CompactStatusText) ? status.StatusText : status.CompactStatusText;
            MiniStatus.Text = compactLine ?? "";
        }

        PauseGlyph.Text = status.RemindersPaused ? "\uE768" : "\uE769";
        PauseLabel.Text = status.RemindersPaused ? "恢复" : "暂停";
        PauseButton.ToolTip = status.RemindersPaused ? "恢复提醒" : "今天剩余时间不再提醒";
    }

    private void UpdateAmountText() => AmountText.Text = Math.Round(DisplayMl).ToString("N0");

    private static void Animate(DependencyObject target, DependencyProperty dp, double to, bool animate)
    {
        if (target is not IAnimatable animatable) return;
        if (!animate) { animatable.BeginAnimation(dp, null); target.SetValue(dp, to); return; }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animatable.BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
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

    private async void DrinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_drinkRequestInFlight || _status is null || !FloatingWidgetView.CanDrink(_status.ConsumedMl, _status.GoalMl)) return;
        await LogDrinkAsync(FloatingWidgetView.CurrentCupMl(_status));
    }

    private async void CustomButton_Click(object sender, RoutedEventArgs e)
    {
        var cup = _status is null ? 370 : FloatingWidgetView.CurrentCupMl(_status);
        var dialog = new AmountWindow(initial: cup, cup: cup) { Owner = this };
        if (dialog.ShowDialog() == true) await LogDrinkAsync(dialog.AmountMl);
    }

    private async Task LogDrinkAsync(int amount)
    {
        if (_drinkRequestInFlight) return;
        _drinkRequestInFlight = true;
        ApplyStatus(_status ?? new StatusResponse());
        try
        {
            var result = await _api.LogAmountAsync(amount);
            if (!result.Ok || result.Value is null)
            {
                ShowSnack(string.IsNullOrWhiteSpace(result.Error) ? "没能记下这一杯" : result.Error);
                return;
            }
            ShowSnack(string.IsNullOrWhiteSpace(result.Value.Message) ? $"已记 {result.Value.RecordedMl} ml" : result.Value.Message);
            await RefreshAsync();
        }
        finally
        {
            _drinkRequestInFlight = false;
            if (_status is not null) ApplyStatus(_status);
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pauseRequestInFlight) return;
        _pauseRequestInFlight = true;
        PauseButton.IsEnabled = false;
        try
        {
            var settings = await _api.GetSettingsAsync();
            if (!settings.Ok || settings.Value is null)
            {
                ShowSnack(settings.Error ?? "无法读取设置");
                return;
            }
            var update = SettingsEditor.FromResponse(settings.Value);
            update.PauseOnly = true;
            if (settings.Value.RemindersPaused)
            {
                update.RemindersPaused = false;
                update.PauseUntil = null;
            }
            else
            {
                var day = _status is not null
                    && DateTime.TryParse(_status.Now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var now)
                    ? now.Date
                    : DateTime.Today;
                update.RemindersPaused = true;
                update.PauseUntil = day.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            }
            var saved = await _api.SaveSettingsAsync(update);
            if (!saved.Ok) ShowSnack(saved.Error ?? "没能暂停提醒");
        }
        finally
        {
            _pauseRequestInFlight = false;
            await RefreshAsync();
        }
    }

    private async void RecordsButton_Click(object sender, RoutedEventArgs e)
    {
        var cup = _status is null ? 370 : FloatingWidgetView.CurrentCupMl(_status);
        DateTime? serverNow = null;
        if (_status is not null
            && DateTime.TryParse(_status.Now, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var now))
            serverNow = now;
        var dialog = new RecordsWindow(_api, cup, serverNow) { Owner = this };
        dialog.ShowDialog();
        await RefreshAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeTransitioning || _editing) return;
        if (_config.CompactMode || WindowPlacement.Saved(_config.Display().ToSettings()) is null) SavePosition();
        _config.CompactMode = !_config.CompactMode;
        ApplyMode(animate: true);
        _config.Save();
    }

    private void AmountText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
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

    private void BeginEditTotal()
    {
        if (_editing || _status is null) return;
        _editing = true;
        var editor = Editor;
        editor.Text = _status.ConsumedMl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EditorHost.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        SetEditHint(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Activate();
            editor.Focus();
            Keyboard.Focus(editor);
            editor.SelectAll();
        });
    }

    private static bool IsInside(DependencyObject start, DependencyObject ancestor)
    {
        for (var current = start; current is not null; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current))
        {
            if (current == ancestor) return true;
        }
        return false;
    }

    private void SetEditHint(string? error)
    {
        HintText.Text = error ?? "回车确认 · Esc 取消";
        if (error is null) HintText.ClearValue(ForegroundProperty);
        else HintText.SetResourceReference(ForegroundProperty, "Danger");
    }

    private async void EndEditTotal(bool commit)
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
            await SetTotalAsync(total);
        }
        else
        {
            _editing = false;
            CloseEditor();
            await RefreshAsync();
        }
    }

    private async Task SetTotalAsync(int total)
    {
        var result = await _api.SetTodayTotalAsync(total);
        if (!result.Ok || result.Value is null)
        {
            ShowSnack(string.IsNullOrWhiteSpace(result.Error) ? "没能修正" : result.Error);
            await RefreshAsync();
            return;
        }
        if (result.Value.Changed)
        {
            var delta = result.Value.DeltaMl;
            var sign = delta > 0 ? "+" : "−";
            ShowSnack($"已修正为 {result.Value.ConsumedMl:N0} ml（{sign}{Math.Abs(delta):N0}）");
        }
        await RefreshAsync();
    }

    private void CloseEditor()
    {
        AmountEditor.Visibility = Visibility.Collapsed;
        MiniEditor.Visibility = Visibility.Collapsed;
        AmountHit.Visibility = Visibility.Visible;
        MiniAmountHit.Visibility = Visibility.Visible;
        GoalText.ClearValue(ForegroundProperty);
        MiniStatus.ClearValue(ForegroundProperty);
        Keyboard.ClearFocus();
        Focus();
    }

    private void AmountEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndEditTotal(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndEditTotal(false); e.Handled = true; }
        else if (e.Key == Key.Space) e.Handled = true;
    }

    private void AmountEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_editing) return;
        // A null target is the WinForms/WPF focus bounce, not the user leaving the box.
        if (e.NewFocus is null) return;
        if (e.NewFocus is DependencyObject next && IsInside(next, Editor)) return;
        EndEditTotal(true);
    }

    private void AmountEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_editing) return;
        SetEditHint(WaterCalculator.TryParseTotal(Editor.Text, out _, out var error) ? null : error);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_modeTransitioning || _editing || e.OriginalSource is System.Windows.Controls.TextBox) return;
        if (e.Key is Key.Space or Key.Enter) { DrinkButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
