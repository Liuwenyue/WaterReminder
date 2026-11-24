using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using WaterReminder;
using WaterReminder.Client;
using WaterReminder.Services;

internal static class Program
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int passed;

    [STAThread]
    static int Main(string[] args)
    {
        Window? window = null;
        var configFile = Path.Combine(Path.GetTempPath(), $"water-position-{Guid.NewGuid():N}.json");
        try
        {
            var assembly = Assembly.Load("WaterReminder.Client");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = args.Contains("--dark", StringComparer.Ordinal) ? "Dark.xaml" : "Light.xaml";
            foreach (var resource in new[] { theme, "Controls.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WaterReminder.Client;component/Themes/" + resource)
                });
            var configType = assembly.GetType("WaterReminder.Client.ClientConfig")!;
            File.WriteAllText(configFile, """{"serverUrl":"http://localhost/","apiToken":"ui-test-only"}""");
            var config = configType.GetMethod("Load")!.Invoke(null, new object[] { configFile })!;
            using var http = new HttpClient(new OfflineHandler());
            using var api = new ReminderApiClient("http://localhost/", "ui-test-only", http);
            var type = assembly.GetType("WaterReminder.Client.MainWindow")!;
            Window Create(object settings)
            {
                var result = (Window)Activator.CreateInstance(type, Flags, null,
                    new object[] { api, settings, (Action)(() => { }) }, null)!;
                result.ShowActivated = false;
                result.Topmost = false;
                return result;
            }
            window = Create(config);
            window.Show();
            Pump(200);
            var amountText = (TextBlock)window.FindName("AmountText");
            var goalText = (TextBlock)window.FindName("GoalText");
            var percentText = (TextBlock)window.FindName("PercentText");
            amountText.Text = "1,110";
            goalText.Text = "/ 3,000 ml";
            percentText.Text = "37%";
            window.UpdateLayout();
            AssertTextFits(amountText, "expanded amount");
            AssertTextFits(goalText, "expanded goal");
            AssertInside(window, percentText, "expanded percent");
            window.Left = 60;
            window.Top = 60;
            var fullHeight = window.ActualHeight;
            bool Busy() => (bool)type.GetField("_modeTransitioning", Flags)!.GetValue(window)!;
            bool Compact() => (bool)configType.GetProperty("CompactMode")!.GetValue(config)!;
            void Toggle() => type.GetMethod("CompactButton_Click", Flags)!.Invoke(window,
                new object[] { window, new RoutedEventArgs() });
            void Wait() { Pump(650); Check(!Busy(), "transition completes"); }
            void At(double left, double top, string label) =>
                Check(Math.Abs(window.Left - left) < 1 && Math.Abs(window.Top - top) < 1, label);
            void Hide() => type.GetMethod("Hide", Flags | BindingFlags.DeclaredOnly)!.Invoke(window, null);

            var intermediate = false;
            var samples = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
            samples.Tick += (_, _) =>
            {
                if (Busy() && window.SizeToContent == SizeToContent.Manual
                    && DependencyPropertyHelper.GetValueSource(window, Window.HeightProperty).IsAnimated
                    && window.ActualHeight > 115 && window.ActualHeight < fullHeight - 2)
                    intermediate = true;
            };
            samples.Start();
            Toggle();
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(Busy(), "switch starts animation");
                Toggle();
                Check(Compact(), "rapid click does not reverse transition");
            }
            Wait();
            samples.Stop();
            if (SystemParameters.ClientAreaAnimation)
                Check(intermediate, "animated collapse has an intermediate height");
            var compactHeight = window.ActualHeight;
            var miniAmount = (TextBlock)window.FindName("MiniAmount");
            var miniGoal = (TextBlock)window.FindName("MiniGoal");
            var miniPercent = (TextBlock)window.FindName("MiniPercent");
            miniAmount.Text = "1,110";
            miniGoal.Text = " / 3,000 ml";
            miniPercent.Text = "37%";
            window.UpdateLayout();
            AssertTextFits(miniAmount, "compact amount");
            AssertTextFits(miniGoal, "compact goal");
            AssertInside(window, miniPercent, "compact percent");
            AssertInside(window, miniGoal, "compact goal");
            Check(compactHeight < fullHeight, "compact natural height is restored");
            Check(window.SizeToContent == SizeToContent.Height
                && !DependencyPropertyHelper.GetValueSource(window, Window.HeightProperty).IsAnimated,
                "window returns to natural layout");

            // Do not call SavePosition: the expand button itself must capture the current compact position.
            var areas = (IReadOnlyList<ScreenRect>)type.GetMethod("WorkAreas", Flags)!.Invoke(window, null)!;
            var area = areas[0];
            var anchorLeft = area.Right - window.ActualWidth - 8;
            var anchorTop = area.Bottom - compactHeight;
            window.Left = anchorLeft;
            window.Top = anchorTop;
            for (var i = 0; i < 3; i++)
            {
                Toggle(); Wait();
                Check(!Compact() && window.Top + window.ActualHeight <= area.Bottom + 1,
                    "expanded window stays above taskbar");
                Check(window.Top < anchorTop - 2, "expansion temporarily moves up from compact anchor");
                Toggle(); Wait();
                At(anchorLeft, anchorTop, "collapse returns to original compact anchor without drift");
            }

            window.Left = area.Left + 40;
            window.Top = area.Top + 70;
            type.GetMethod("SavePosition", Flags)!.Invoke(window, null);
            Toggle(); Wait();
            Toggle(); Wait();
            At(area.Left + 40, area.Top + 70, "moving compact widget updates its anchor");

            // Test hiding in both directions, including during the initial fade.
            foreach (var expectedCompact in new[] { false, true })
            {
                Toggle();
                Check(Compact() == expectedCompact, "toggle selects expected target mode");
                Hide();
                Pump(100);
                window.Show();
                Pump(100);
                Check(!Busy() && Compact() == expectedCompact && window.Opacity == 1,
                    "hide and restore preserves chosen mode");
            }
            At(area.Left + 40, area.Top + 70, "hiding mid-transition preserves compact anchor");
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configFile)))
            {
                Check(doc.RootElement.GetProperty("compactMode").GetBoolean(),
                    "compact mode preference is saved");
                Check(Math.Abs(doc.RootElement.GetProperty("windowTop").GetDouble() - (area.Top + 70)) < 1,
                    "saved anchor is not expanded placement");
            }

            configType.GetProperty("CompactMode")!.SetValue(config, false);
            type.GetMethod("ApplyMode", Flags)!.Invoke(window, new object[] { false });
            Check(!Busy(), "switch without animation completes immediately");
            configType.GetProperty("CompactMode")!.SetValue(config, true);
            type.GetMethod("ApplyMode", Flags)!.Invoke(window, new object[] { false });
            At(area.Left + 40, area.Top + 70, "non-animated collapse uses compact anchor");

            type.GetMethod("ForceClose")!.Invoke(window, null);
            config = configType.GetMethod("Load")!.Invoke(null, new object[] { configFile })!;
            window = Create(config);
            window.Show();
            Pump(100);
            At(area.Left + 40, area.Top + 70, "restart restores compact anchor");

            // A professional-mode drag is temporary. Exercise every real monitor, including
            // negative coordinates, and restart with the professional window selected.
            foreach (var monitor in areas)
            {
                var savedLeft = monitor.Right - window.ActualWidth - 12;
                var savedTop = monitor.Bottom - compactHeight - 12;
                window.Left = savedLeft;
                window.Top = savedTop;
                type.GetMethod("SavePosition", Flags)!.Invoke(window, null);
                Console.WriteLine($"ANCHOR left={savedLeft:F2} top={savedTop:F2} monitor={monitor}");
                Toggle(); Wait();
                window.Left = monitor.Left + 50;
                window.Top = monitor.Top + 50;
                type.GetMethod("SavePosition", Flags)!.Invoke(window, null);
                using (var anchorDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configFile)))
                    Check(Math.Abs(anchorDoc.RootElement.GetProperty("windowLeft").GetDouble() - savedLeft) < 1
                        && Math.Abs(anchorDoc.RootElement.GetProperty("windowTop").GetDouble() - savedTop) < 1,
                        "dragging professional mode preserves the original compact anchor");
                Hide(); window.Show(); Pump(100);
                Toggle(); Wait();
                At(savedLeft, savedTop, "collapse after professional drag and hide restores compact anchor");
                Toggle();
                Pump(160);
                type.GetMethod("ForceClose")!.Invoke(window, null);
                config = configType.GetMethod("Load")!.Invoke(null, new object[] { configFile })!;
                window = Create(config);
                window.Show(); Pump(200);
                Check(!Compact() && !Busy(), "restart during expansion restores professional mode");
                Toggle(); Wait();
                At(savedLeft, savedTop, "collapse after expansion restart restores compact anchor");
            }
            var familyHandler = new FamilyHandler();
            using var familyHttp = new HttpClient(familyHandler);
            using var familyApi = new ReminderApiClient("http://localhost/", "ui-test-only", familyHttp);
            var history = new RecordsWindow(familyApi, 370, new DateTime(2026, 11, 18, 12, 0, 0))
                { Owner = window, Topmost = false, ShowActivated = false };
            history.Show();
            try
            {
                Pump(250);
                var memberPicker = (ComboBox)history.FindName("MemberPicker");
                var historyPicker = (ComboBox)history.FindName("HistoryPicker");
                var clear = (Button)history.FindName("ClearButton");
                var summary = (TextBlock)history.FindName("SummaryText");
                Check(memberPicker.Items.Count == 2 && historyPicker.Items.Count == 2, "owner sees family and date summaries");
                foreach (var picker in new[] { memberPicker, historyPicker })
                {
                    picker.IsDropDownOpen = true;
                    Pump(100);
                    var popup = picker.Template.FindName("PART_Popup", picker) as Popup;
                    Check(popup is { IsOpen: true, Child: FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } }
                        && picker.ItemContainerGenerator.ContainerFromIndex(1) is ComboBoxItem { IsVisible: true },
                        "themed dropdown opens and renders its options");
                    picker.IsDropDownOpen = false;
                    Pump(100);
                    Check(popup is { IsOpen: false }, "themed dropdown closes");
                }
                Check(clear.IsEnabled && summary.Text.Contains("300"), "today's own record is editable");
                history.UpdateLayout();
                var dayButtons = ((UniformGrid)history.FindName("PeriodGrid")).Children.OfType<Button>().Where(day => day.ActualWidth > 0).ToList();
                Check(dayButtons.Count >= 7, "week calendar renders its days");
                foreach (var day in dayButtons)
                    AssertInside(history, day, "calendar day");
                historyPicker.SelectedIndex = 1;
                Pump(80);
                Check(!clear.IsEnabled && summary.Text.Contains("500"), "past record opens read-only");
                memberPicker.SelectedIndex = 1;
                Pump(80);
                Check(summary.Text.Contains("700") && clear.Visibility == Visibility.Collapsed, "member's today opens read-only");
                var memberSettings = (Button)history.FindName("MemberSettingsButton");
                var localBeforeMemberEdit = File.ReadAllText(configFile);
                void InspectMemberSettings(Action<SettingsWindow> inspect)
                {
                    Exception? failure = null;
                    var seen = false;
                    var dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    dialogTimer.Tick += (_, _) =>
                    {
                        var dialog = app.Windows.OfType<SettingsWindow>().FirstOrDefault(w => w.Owner == history);
                        if (dialog is null) return;
                        dialogTimer.Stop();
                        seen = true;
                        try { inspect(dialog); }
                        catch (Exception error) { failure = error; }
                        finally { if (dialog.IsVisible) dialog.Close(); }
                    };
                    dialogTimer.Start();
                    memberSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(100);
                    dialogTimer.Stop();
                    if (failure is not null) throw failure;
                    Check(seen, "selected member's settings dialog opens");
                }
                InspectMemberSettings(AssertSettingsReadable);
                InspectMemberSettings(dialog =>
                {
                    Check(dialog.Title.Contains("家人甲"), "member settings identify the selected person");
                    Check(((Border)dialog.FindName("LocalPreferencesCard")).Visibility == Visibility.Collapsed
                        && !((CheckBox)dialog.FindName("FloatingBox")).IsVisible
                        && !((CheckBox)dialog.FindName("AutoStartBox")).IsVisible,
                        "member settings hide this computer's preferences");
                    var goal = dialog.FindName("GoalStepper");
                    goal.GetType().GetProperty("Value")!.SetValue(goal, 2400);
                    ((PasswordBox)dialog.FindName("MemberPushToken")).Password = "   ";
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Check(familyHandler.SettingsPuts == 1 && familyHandler.LastUpdate?.Settings.DailyGoalMl == 2400
                    && familyHandler.LastUpdate.PushWardToken is null,
                    "member save uses the member endpoint and preserves a blank phone token");
                InspectMemberSettings(dialog =>
                {
                    var goal = dialog.FindName("GoalStepper");
                    Check((int)goal.GetType().GetProperty("Value")!.GetValue(goal)! == 2400,
                        "reopening the member settings reads the saved goal");
                    Check(((PasswordBox)dialog.FindName("MemberPushToken")).Password.Length == 0,
                        "member phone tokens are never filled back into the form");
                });
                Check(File.ReadAllText(configFile) == localBeforeMemberEdit && familyHandler.OwnerSettingsRequests == 0,
                    "editing a member does not change owner's settings or local client preferences");
                familyHandler.PendingSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
                memberSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!memberSettings.IsEnabled, "member settings button is disabled while loading");
                memberPicker.SelectedIndex = 0;
                familyHandler.PendingSettings.SetResult(FamilyHandler.SettingsResponse(2400));
                familyHandler.PendingSettings = null;
                Pump(150);
                Check(!app.Windows.OfType<SettingsWindow>().Any() && !memberSettings.IsEnabled,
                    "switching back to owner discards an older member settings response");
                memberPicker.SelectedIndex = 1;
                Pump(80);
                familyHandler.RejectSettings = true;
                memberSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(100);
                Check(!app.Windows.OfType<SettingsWindow>().Any() && summary.Text.Contains("家人甲")
                    && summary.Text.Contains("不可用"), "unavailable member settings show an error for that person");
                familyHandler.RejectSettings = false;
                var invite = new FamilyInviteWindow(familyApi) { Owner = history };
                invite.Show();
                try
                {
                    ((TextBox)invite.FindName("MemberName")).Text = "家人乙";
                    var create = (Button)invite.FindName("CreateButton");
                    create.Focus();
                    Pump(40);
                    var buttonBorder = (Border)create.Template.FindName("bd", create);
                    Check(buttonBorder.BorderThickness == create.BorderThickness,
                        "focusing a clicked button does not add a blue border");
                    Check(ReferenceEquals(create.FocusVisualStyle, app.FindResource("ButtonKeyboardFocus")),
                        "buttons retain the separate keyboard-navigation focus visual");
                    create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump(80);
                    var access = (TextBox)invite.FindName("AccessCode");
                    Check(access.Visibility == Visibility.Visible && access.Text == "demo-code", "invitation shows the returned access code");
                }
                finally { invite.Close(); }
            }
            finally { history.Close(); }
            Console.WriteLine($"UI_CHECKS_PASSED={passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            window?.GetType().GetMethod("ForceClose")?.Invoke(window, null);
            if (File.Exists(configFile)) File.Delete(configFile);
            Application.Current?.Shutdown();
        }
    }


    static void AssertSettingsReadable(SettingsWindow dialog)
    {
        var lunch = (CheckBox)dialog.FindName("LunchBox");
        lunch.IsChecked = true;
        lunch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        dialog.UpdateLayout();
        ((ScrollViewer)dialog.FindName("SettingsScroll")).ScrollToEnd();
        dialog.UpdateLayout();
        foreach (var name in new[]
        {
            "GoalStepper", "WorkCupStepper", "HomeCupStepper", "StartStepper", "EndStepper",
            "LunchStartStepper", "LunchEndStepper", "NoDrinkStepper", "GapStepper", "QuietStepper"
        })
        {
            var stepper = (FrameworkElement)dialog.FindName(name);
            AssertInside(dialog, stepper, name);
            var box = FindChild<TextBox>(stepper);
            var shown = box?.Text ?? "";
            Check(box is not null && TextFits(box), name + " shows '" + shown + "' in full");
        }
        foreach (var name in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
        {
            var chip = (FrameworkElement)dialog.FindName(name);
            AssertInside(dialog, chip, "weekday " + name);
            Check(chip.ActualWidth >= 28 && chip.ActualHeight >= 24, "weekday " + name + " is large enough to read");
        }
        var sunday = (FrameworkElement)dialog.FindName("Sun");
        var save = (FrameworkElement)dialog.FindName("SaveButton");
        Check(sunday.TranslatePoint(new Point(0, sunday.ActualHeight), dialog).Y
            <= save.TranslatePoint(new Point(0, 0), dialog).Y + 0.5,
            "weekday chips sit above the save button");
    }



    static void AssertInside(Window window, FrameworkElement element, string name)
    {
        var topLeft = element.TranslatePoint(new Point(1, 1), window);
        var bottomRight = element.TranslatePoint(new Point(Math.Max(0, element.ActualWidth - 1), Math.Max(0, element.ActualHeight - 1)), window);
        Check(element.IsVisible && element.ActualWidth > 8 && element.ActualHeight > 8
            && topLeft.X >= -0.5 && topLeft.Y >= -0.5
            && bottomRight.X <= window.ActualWidth + 1
            && bottomRight.Y <= window.ActualHeight + 1,
            name + " is fully inside the window");
    }

    static void AssertTextFits(TextBlock block, string name)
    {
        var needed = Measure(block.Text, block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch, block.FontSize, block);
        Check(block.ActualWidth + 2 >= needed, name + " is fully visible");
    }

    static bool TextFits(TextBox box)
    {
        var needed = Measure(box.Text, box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch, box.FontSize, box);
        return box.ActualWidth - box.Padding.Left - box.Padding.Right + 2 >= needed;
    }

    static double Measure(string? text, FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch, double size, Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        return new FormattedText(
            text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, style, weight, stretch),
            size,
            Brushes.Black,
            dpi).WidthIncludingTrailingWhitespace;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    sealed class FamilyHandler : HttpMessageHandler
    {
        public int SettingsPuts;
        public int OwnerSettingsRequests;
        public int SavedGoal = 3000;
        public FamilySettingsUpdate? LastUpdate;
        public TaskCompletionSource<HttpResponseMessage>? PendingSettings;
        public bool RejectSettings;

        public static HttpResponseMessage SettingsResponse(int goal) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new ClientSettingsResponse
            {
                DailyGoalMl = goal, WorkCupMl = 370, HomeCupMl = 500, WorkStart = "08:30", WorkEnd = "18:00",
                LunchStart = "12:00", LunchEnd = "13:00", QuietStart = "22:00", MinReminderGapMinutes = 20,
                ActiveDays = [true, true, true, true, true, false, false], MobilePushEnabled = true
            }, ReminderApiClient.Json), System.Text.Encoding.UTF8, "application/json")
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string id = "3d3f62fe-867e-44fd-a1da-0609d1de23a5";
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/settings") OwnerSettingsRequests++;
            if (path == $"/api/family/{id}/settings")
            {
                if (request.Method == HttpMethod.Get && PendingSettings is not null) return PendingSettings.Task;
                if (RejectSettings) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = new StringContent("""{"error":"该成员已不可用"}""", System.Text.Encoding.UTF8, "application/json") });
                if (request.Method == HttpMethod.Put)
                {
                    LastUpdate = System.Text.Json.JsonSerializer.Deserialize<FamilySettingsUpdate>(
                        request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(), ReminderApiClient.Json)!;
                    SavedGoal = LastUpdate.Settings.DailyGoalMl;
                    SettingsPuts++;
                }
                return Task.FromResult(SettingsResponse(SavedGoal));
            }
            var family = path.StartsWith("/api/family/", StringComparison.Ordinal);
            var yesterday = request.RequestUri.Query.Contains("2026-11-17", StringComparison.Ordinal);
            var day = yesterday ? "2026-11-17" : "2026-11-18";
            var amount = family ? 700 : yesterday ? 500 : 300;
            var json = path switch
            {
                "/api/family" when request.Method == HttpMethod.Post => $$"""
                    {"member":{"id":"{{id}}","name":"家人乙"},"accessCode":"demo-code"}
                    """,
                "/api/family" => $$"""
                    {"isOwner":true,"members":[{"id":"00000000-0000-0000-0000-000000000000","name":"我"},{"id":"{{id}}","name":"家人甲"}]}
                    """,
                "/api/history" => """
                    {"days":[{"date":"2026-11-18","consumedMl":300,"eventCount":1},{"date":"2026-11-17","consumedMl":500,"eventCount":1}]}
                    """,
                _ when path.EndsWith("/history") => """
                    {"days":[{"date":"2026-11-18","consumedMl":700,"eventCount":1}]}
                    """,
                _ => $$"""
                    {"date":"{{day}}","consumedMl":{{amount}},"goalMl":3000,"editable":{{(!family && !yesterday).ToString().ToLowerInvariant()}},"events":[{"id":"{{id}}","at":"{{day}}T10:00:00","amountMl":{{amount}},"source":"desktop"}]}
                    """
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
