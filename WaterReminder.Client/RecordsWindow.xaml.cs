using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WaterReminder;
using WaterReminder.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>Daily records and member management.</summary>
public partial class RecordsWindow : Window
{
    private readonly ReminderApiClient _api;
    private readonly int _cupMl;
    private DateOnly _today;
    private readonly DateTime _serverNow;
    private DateOnly _selected;
    private TodayRecordsResponse? _records;
    private Guid? _member;
    private int _viewVersion;
    private int _detailVersion;
    private bool _updatingPickers;
    private CancellationTokenSource? _memberSettingsRequest;
    private int _memberSettingsVersion;
    private bool _closed;
    private bool _recordMutationInFlight;
    private bool _monthView;
    private readonly Dictionary<DateOnly, HistoryDayResponse> _history = new();
    private readonly ClickGuard _clicks = new();

    public RecordsWindow(ReminderApiClient api, int cupMl, DateTime? serverNow = null)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _api = api;
        _cupMl = cupMl;
        _serverNow = serverNow ?? DateTime.Now;
        _today = DateOnly.FromDateTime(_serverNow);
        _selected = _today;
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        DialogLayout.KeepOnScreen(this);
        Closed += (_, _) => { _closed = true; CancelMemberSettingsLoad(); };
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            await LoadFamilyAsync();
            await LoadHistoryAsync();
        };
    }

    private async Task ReloadAsync()
    {
        var version = ++_detailVersion;
        var date = _selected;
        var member = _member;
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _records = null;
        ClearButton.IsEnabled = false;
        RecordsList.ItemsSource = null;
        DateLabel.Text = key;
        SummaryText.Text = "正在读取…";
        EmptyState.Visibility = Visibility.Collapsed;
        var result = member is { } memberId
            ? await _api.GetFamilyRecordsAsync(memberId, key)
            : await _api.GetRecordsAsync(key);
        if (version != _detailVersion) return;
        if (!result.Ok || result.Value is null)
        {
            SummaryText.Text = result.Error ?? "无法读取记录";
            return;
        }
        _records = result.Value;
        var editable = result.Value.Editable && (member is not null || date == _today);
        var events = result.Value.Events
            .OrderByDescending(x => x.At)
            .Select(x => new RecordItem(x, editable))
            .ToList();
        RecordsList.ItemsSource = events;
        var count = events.Count;
        var dateLabel = DateTime.TryParse(result.Value.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("M月d日 dddd")
            : result.Value.Date;
        DateLabel.Text = dateLabel;
        TitleText.Text = date == _today ? "今日记录" : "历史记录";
        var goal = result.Value.GoalKnown ? $"{result.Value.GoalMl:N0} ml" : "目标未记录";
        SummaryText.Text = count == 0
            ? (editable ? dateLabel : $"{dateLabel} · 只读")
            : $"共 {count} 条 · 合计 {result.Value.ConsumedMl:N0} / {goal}" + (editable ? "" : " · 只读");
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = date == _today ? "今天还没有喝水记录" : "这一天没有记录";
        EmptyHint.Text = editable && member is null ? "在主窗口点「喝一杯」就会出现在这里" : "可选择其他日期查看";
        ClearButton.IsEnabled = editable && member is null && count > 0;
        ClearButton.Visibility = editable && member is null ? Visibility.Visible : Visibility.Collapsed;
        NextDayButton.IsEnabled = _selected < _today;
        PrevDayButton.IsEnabled = true;
        ShowPeriod();
    }

    private async Task LoadFamilyAsync()
    {
        var response = await _api.GetFamilyAsync();
        if (!response.Ok || response.Value is not { IsOwner: true } family) return;
        _updatingPickers = true;
        MemberPicker.ItemsSource = family.Members.Select(member => new MemberOption(member)).ToList();
        MemberPicker.SelectedItem = ((IEnumerable<MemberOption>)MemberPicker.ItemsSource)
            .FirstOrDefault(option => option.Member.Id == (_member ?? Guid.Empty));
        FamilyPanel.Visibility = Visibility.Visible;
        _updatingPickers = false;
        UpdateMemberActions();
        await LoadProgressAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var version = ++_viewVersion;
        var response = _member is { } member
            ? await _api.GetFamilyHistoryAsync(member, 366)
            : await _api.GetHistoryAsync(366);
        if (version != _viewVersion) return;
        _updatingPickers = true;
        var options = new List<HistoryOption>();
        if (response.Ok && response.Value is { } history)
        {
            foreach (var day in history.Days)
            {
                if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                var goal = day.GoalKnown ? $"{day.ConsumedMl:N0} / {day.GoalMl:N0} ml" : $"{day.ConsumedMl:N0} ml · 目标未记录";
                options.Add(new(date, $"{date:MM月dd日}   {goal}   ·   {day.EventCount} 条"));
            }
        }
        if (options.All(option => option.Date != _today)) options.Insert(0, new(_today, "今日 · 无记录"));
        HistoryPicker.ItemsSource = options;
        HistoryPicker.SelectedItem = options.FirstOrDefault(option => option.Date == _selected);
        HistoryPicker.Visibility = response.Ok ? Visibility.Visible : Visibility.Collapsed;
        _updatingPickers = false;
        _history.Clear();
        if (response.Ok && response.Value is { } loaded)
        {
            foreach (var day in loaded.Days)
                if (DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                    _history[parsed] = day;
        }
        ShowPeriod();
    }

    private async void Member_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || MemberPicker.SelectedItem is not MemberOption option) return;
        CancelMemberSettingsLoad();
        _member = option.Member.Id == Guid.Empty ? null : option.Member.Id;
        _selected = _today;
        UpdateMemberActions();
        HistoryPicker.Visibility = Visibility.Collapsed;
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync(), LoadProgressAsync());
    }

    private async void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || HistoryPicker.SelectedItem is not HistoryOption option) return;
        _selected = option.Date;
        await ReloadAsync();
    }

    private async void AddMember_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        var dialog = new FamilyInviteWindow(_api) { Owner = this };
        dialog.ShowDialog();
        await LoadFamilyAsync();
    
        }
        finally { _clicks.Exit(); }
    }

    private async void ResetCode_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (MemberPicker.SelectedItem is not MemberOption option || option.Member.Id == Guid.Empty || option.Member.Archived)
            return;
        if (!ConfirmDialog.Ask(this, $"重置 {option.Member.Name} 的访问码？", "旧访问码立即失效，该成员需要用新访问码重新连接，饮水历史仍保留。", "重置", danger: true))
            return;
        var result = await _api.ResetMemberAccessCodeAsync(option.Member.Id);
        if (!result.Ok || result.Value is null)
        {
            SummaryText.Text = result.Error ?? "重置失败，请检查服务端连接。";
            return;
        }
        new FamilyInviteWindow(_api, result.Value) { Owner = this }.ShowDialog();
    
        }
        finally { _clicks.Exit(); }
    }

    private async void PushTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        var id = MemberPicker.SelectedItem is MemberOption option ? option.Member.Id : Guid.Empty;
        PushTestButton.IsEnabled = false;
        FamilyProgressText.Text = "正在发送连接测试…";
        var result = await _api.SendPushTestAsync(id);
        PushTestButton.IsEnabled = true;
        FamilyProgressText.Text = result.Ok ? result.Value?.Message ?? "已发送" : result.Error ?? "测试失败，请检查服务端连接。";
        await LoadProgressAsync();
    
        }
        finally { _clicks.Exit(); }
    }

    private async Task LoadProgressAsync()
    {
        var response = await _api.GetFamilyProgressAsync();
        if (!response.Ok || response.Value is null)
        {
            if (FamilyPanel.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(FamilyProgressText.Text))
                FamilyProgressText.Text = response.Error ?? "";
            return;
        }
        var id = _member ?? Guid.Empty;
        var row = response.Value.Members?.FirstOrDefault(item => item.Member.Id == id);
        if (row is null) return;
        var amount = row.GoalMl > 0 ? $"{row.ConsumedMl:N0} / {row.GoalMl:N0} ml" : $"{row.ConsumedMl:N0} ml";
        var last = DateTime.TryParse(row.LastRecordAt, out var at) ? at.ToString("M月d日 HH:mm") : "还没有记录";
        FamilyProgressText.Text = $"今日 {amount} · 本周有记录 {row.WeekRecordedDays} 天，达标 {row.WeekAchievedDays} 天 · 最近 {last} · {row.Push.Message}";
    }

    private void UpdateMemberActions()
    {
        var active = MemberPicker.SelectedItem is MemberOption { Member: { Archived: false } } option
            && option.Member.Id != Guid.Empty;
        ArchiveMemberButton.IsEnabled = active;
        ResetCodeButton.IsEnabled = active;
        PushTestButton.IsEnabled = MemberPicker.SelectedItem is MemberOption { Member.Archived: false };
        MemberSettingsButton.IsEnabled = active && _memberSettingsRequest is null;
        MemberSettingsButton.Content = _memberSettingsRequest is null ? "成员设置" : "读取中…";
    }

    private void CancelMemberSettingsLoad()
    {
        ++_memberSettingsVersion;
        _memberSettingsRequest?.Cancel();
        _memberSettingsRequest = null;
        UpdateMemberActions();
    }

    private async void MemberSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (_memberSettingsRequest is not null
            || MemberPicker.SelectedItem is not MemberOption { Member: { Archived: false } } option
            || option.Member.Id == Guid.Empty) return;
        var member = option.Member;
        var version = ++_memberSettingsVersion;
        using var request = new CancellationTokenSource();
        _memberSettingsRequest = request;
        UpdateMemberActions();
        try
        {
            var settingsTask = _api.GetFamilySettingsAsync(member.Id, request.Token);
            var todayTask = _api.GetFamilyRecordsAsync(member.Id, _today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.Token);
            await Task.WhenAll(settingsTask, todayTask);
            if (_closed || version != _memberSettingsVersion || _member != member.Id) return;
            var settings = await settingsTask;
            var today = await todayTask;
            if (!settings.Ok || settings.Value is null || !today.Ok || today.Value is null)
            {
                SummaryText.Text = $"{member.Name}：{settings.Error ?? today.Error ?? "无法读取成员设置，请重试。"}";
                if (settings.StatusCode is 403 or 404 or 409) await LoadFamilyAsync();
                return;
            }

            _memberSettingsRequest = null;
            UpdateMemberActions();
            SettingsWindow? dialog = null;
            dialog = new SettingsWindow(settings.Value, false, false, async update =>
            {
                if (_closed || _member != member.Id
                    || MemberPicker.SelectedItem is not MemberOption { Member: { Archived: false } } selected
                    || selected.Member.Id != member.Id)
                    return "成员已切换或停用，请重新打开设置。";
                var saved = await _api.SaveFamilySettingsAsync(member.Id, new FamilySettingsUpdate(update, dialog!.PushWardToken));
                return saved.Ok ? null : saved.Error ?? "无法保存该成员设置，请重试。";
            }, _serverNow, today.Value.ConsumedMl, member.Name) { Owner = this };
            if (dialog.ShowDialog() == true && !_closed && _member == member.Id)
                await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
        }
        finally
        {
            if (version == _memberSettingsVersion)
            {
                _memberSettingsRequest = null;
                if (!_closed) UpdateMemberActions();
            }
        }
    
        }
        finally { _clicks.Exit(); }
    }

    private async void ArchiveMember_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (MemberPicker.SelectedItem is not MemberOption option || option.Member.Id == Guid.Empty || option.Member.Archived)
            return;
        if (!ConfirmDialog.Ask(this, $"停用 {option.Member.Name}？", "该访问码将失效并停止提醒，已有记录仍可查看。", "停用", danger: true))
            return;
        ArchiveMemberButton.IsEnabled = false;
        if (!await _api.ArchiveFamilyMemberAsync(option.Member.Id))
        {
            SummaryText.Text = "停用失败";
            ArchiveMemberButton.IsEnabled = true;
            return;
        }
        await LoadFamilyAsync();
    
        }
        finally { _clicks.Exit(); }
    }

    private sealed record HistoryOption(DateOnly Date, string Label);

    private sealed record MemberOption(FamilyMember Member)
    {
        public string Label => Member.Archived ? $"{Member.Name}（已停用）" : Member.Name;
    }


    private void WeekView_Click(object sender, RoutedEventArgs e)
    {
        _monthView = false;
        WeekViewButton.IsChecked = true;
        MonthViewButton.IsChecked = false;
        ShowPeriod();
    }

    private void MonthView_Click(object sender, RoutedEventArgs e)
    {
        _monthView = true;
        WeekViewButton.IsChecked = false;
        MonthViewButton.IsChecked = true;
        ShowPeriod();
    }

    private async void PeriodDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateOnly date } || date > _today || date == _selected) return;
        if (!_clicks.Enter()) return;
        try
        {
            _selected = date;
            await ReloadAsync();
        }
        finally { _clicks.Exit(); }
    }

    private void ShowPeriod()
    {
        if (PeriodGrid is null) return;
        var cells = _monthView
            ? HistoryCalendar.Month(_selected, _today, _history)
            : HistoryCalendar.Week(_selected, _today, _history);
        PeriodGrid.Children.Clear();
        if (_monthView)
        {
            foreach (var name in new[] { "\u4e00", "\u4e8c", "\u4e09", "\u56db", "\u4e94", "\u516d", "\u65e5" })
                PeriodGrid.Children.Add(new TextBlock
                {
                    Text = name,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4)
                });
        }
        foreach (var cell in cells)
        {
            if (cell.Date is not { } date)
            {
                PeriodGrid.Children.Add(new Border());
                continue;
            }
            var label = new TextBlock { Text = cell.Label, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            var amountText = new TextBlock
            {
                Text = cell.ConsumedMl > 0 ? cell.ConsumedMl.ToString("N0") : "",
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap
            };
            var amount = new Viewbox
            {
                Child = amountText,
                Stretch = System.Windows.Media.Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxHeight = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 1, 1, 0)
            };
            var button = new Button
            {
                Content = new StackPanel { Children = { label, amount } },
                Tag = date,
                IsEnabled = !cell.Future,
                ToolTip = cell.ConsumedMl > 0 ? cell.ConsumedMl.ToString("N0") + " ml" : "没有记录",
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                Height = 52,
                ClipToBounds = false
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, cell.Selected ? "DayCellSelected" : "DayCell");
            button.Click += PeriodDay_Click;
            PeriodGrid.Children.Add(button);
        }
        var summary = HistoryCalendar.Summary(cells.Where(cell => cell.Date is { } day && day <= _today).Select(cell => cell.Date!.Value), _today, _history);
        PeriodSummary.Text = (_monthView ? "\u672c\u6708\u5408\u8ba1 " : "\u672c\u5468\u5408\u8ba1 ") + summary.Consumed.ToString("N0") + " ml \u00b7 \u8fbe\u6807 " + summary.Achieved + " \u5929";
    }

    private async void PrevDay_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        _selected = _selected.AddDays(-1);
        await ReloadAsync();
    
        }
        finally { _clicks.Exit(); }
    }

    private async void NextDay_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (_selected >= _today) return;
        _selected = _selected.AddDays(1);
        await ReloadAsync();
    
        }
        finally { _clicks.Exit(); }
    }

    private static RecordItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as RecordItem;

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (_recordMutationInFlight || _records is not { Editable: true } records) return;
        if (ItemOf(sender) is not { } item) return;
        var member = _member;
        var title = member is null ? "修改记录" : $"{(MemberPicker.SelectedItem as MemberOption)?.Member.Name} · {records.Date}";
        var dialog = new AmountWindow(initial: Math.Abs(item.Event.AmountMl), cup: _cupMl, title: title, okText: "保存") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var amount = item.Event.AmountMl < 0 ? -dialog.AmountMl : dialog.AmountMl;
        _recordMutationInFlight = true;
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            if (member is { } memberId)
            {
                await ChangeMemberRecordAsync(memberId, records, item, amount);
                return;
            }
            var saved = await _api.EditRecordAsync(item.Event.Id, amount);
            if (!saved.Ok) { SummaryText.Text = saved.Error ?? "没能修改"; return; }
            await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
        }
        finally
        {
            _recordMutationInFlight = false;
            if (sender is Button restore) restore.IsEnabled = true;
        }
    
        }
        finally { _clicks.Exit(); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (_recordMutationInFlight || _records is not { Editable: true } records) return;
        if (ItemOf(sender) is not { } item) return;
        var member = _member;
        var ledger = member is null ? "" : $"{(MemberPicker.SelectedItem as MemberOption)?.Member.Name} · {records.Date} ";
        if (!ConfirmDialog.Ask(this, "删除这条记录？", $"{ledger}{item.TimeText} 的 {item.AmountDisplay} 将被删除。", "删除", danger: true)) return;
        _recordMutationInFlight = true;
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            if (member is { } memberId)
            {
                await ChangeMemberRecordAsync(memberId, records, item, null);
                return;
            }
            var saved = await _api.DeleteRecordAsync(item.Event.Id);
            if (!saved.Ok) { SummaryText.Text = saved.Error ?? "没能删除"; return; }
            await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
        }
        finally
        {
            _recordMutationInFlight = false;
            if (sender is Button restore) restore.IsEnabled = true;
        }
    
        }
        finally { _clicks.Exit(); }
    }

    private async Task ChangeMemberRecordAsync(Guid memberId, TodayRecordsResponse records, RecordItem item, int? amount)
    {
        var saved = await _api.ChangeFamilyRecordAsync(memberId, records.Date, item.Event.Id, amount);
        if (_closed || !ReferenceEquals(_records, records)) return;
        if (!saved.Ok) { SummaryText.Text = saved.Error ?? "无法保存修改。"; return; }
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_clicks.Enter()) return;
        try {
        if (_recordMutationInFlight || _member is not null || _records is not { Editable: true } records || records.Events.Count == 0) return;
        if (!ConfirmDialog.Ask(this, "清空今天的所有记录？", $"{records.Date} 的饮水进度将归零，此操作无法撤销。", "清空", danger: true)) return;
        _recordMutationInFlight = true;
        ClearButton.IsEnabled = false;
        try
        {
            if (_closed || !ReferenceEquals(_records, records)) return;
            var saved = await _api.ClearTodayForDateAsync(records.Date);
            if (_closed || !ReferenceEquals(_records, records)) return;
            if (saved.StatusCode == 409)
            {
                var current = await _api.GetRecordsAsync();
                if (_closed || !ReferenceEquals(_records, records)) return;
                if (current.Value is { } today &&
                    DateOnly.TryParseExact(today.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    _selected = _today = date;
                    var refreshedVersion = _detailVersion + 1;
                    await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
                    if (_closed || refreshedVersion != _detailVersion) return;
                }
                SummaryText.Text = saved.Error ?? "账本日期已变更，请查看今日记录后重新确认清空。";
                return;
            }

            if (!saved.Ok)
            {
                SummaryText.Text = saved.Error ?? "没能清空";
                return;
            }
            await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
        }
        finally
        {
            _recordMutationInFlight = false;
            if (!_closed) ClearButton.IsEnabled = true;
        }
    
        }
        finally { _clicks.Exit(); }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    private sealed class RecordItem
    {
        public RecordEventResponse Event { get; }
        public bool Editable { get; }
        public RecordItem(RecordEventResponse @event, bool editable)
        {
            Event = @event;
            Editable = editable;
        }
        public string AmountDisplay => Event.AmountMl >= 0 ? $"+{Event.AmountMl} ml" : $"{Event.AmountMl} ml";
        public string Glyph => Event.Source == "adjust" ? "\uE70F" : Event.AmountMl < 0 ? "\uE738" : Event.Source == "custom" ? "\uE70F" : "\uE710";
        public string TimeText => DateTime.TryParse(Event.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString("HH:mm") : Event.At;
        public string Detail => $"{TimeText} · {RecordLabels.Describe(Event.Source, Event.AmountMl)}";
        public Visibility ActionsVisibility => Editable ? Visibility.Visible : Visibility.Collapsed;
    }
}
