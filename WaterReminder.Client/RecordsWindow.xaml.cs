using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WaterReminder;
using WaterReminder.Client.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>Records window: today is editable; past days are read-only history.</summary>
public partial class RecordsWindow : Window
{
    private readonly ReminderApiClient _api;
    private readonly int _cupMl;
    private readonly DateOnly _today;
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
        var editable = member is null && date == _today && result.Value.Editable;
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
        TitleText.Text = editable ? "今日记录" : "历史记录";
        SummaryText.Text = count == 0
            ? (editable ? dateLabel : $"{dateLabel} · 只读")
            : $"共 {count} 条 · 合计 {result.Value.ConsumedMl:N0} / {result.Value.GoalMl:N0} ml" + (editable ? "" : " · 只读");
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = editable ? "今天还没有喝水记录" : "这一天没有记录";
        EmptyHint.Text = editable ? "在主窗口点「喝一杯」就会出现在这里" : "可选择其他日期查看";
        ClearButton.IsEnabled = editable && count > 0;
        ClearButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        NextDayButton.IsEnabled = _selected < _today;
        PrevDayButton.IsEnabled = true;
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
    }

    private async Task LoadHistoryAsync()
    {
        var version = ++_viewVersion;
        var response = _member is { } member
            ? await _api.GetFamilyHistoryAsync(member, 30)
            : await _api.GetHistoryAsync(30);
        if (version != _viewVersion) return;
        _updatingPickers = true;
        var options = new List<HistoryOption>();
        if (response.Ok && response.Value is { } history)
        {
            foreach (var day in history.Days)
                if (DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    options.Add(new(date, $"{date:MM月dd日}   {day.ConsumedMl:N0} ml   ·   {day.EventCount} 条"));
        }
        if (options.All(option => option.Date != _today)) options.Insert(0, new(_today, "今日 · 无记录"));
        HistoryPicker.ItemsSource = options;
        HistoryPicker.SelectedItem = options.FirstOrDefault(option => option.Date == _selected);
        HistoryPicker.Visibility = response.Ok ? Visibility.Visible : Visibility.Collapsed;
        _updatingPickers = false;
    }

    private async void Member_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || MemberPicker.SelectedItem is not MemberOption option) return;
        CancelMemberSettingsLoad();
        _member = option.Member.Id == Guid.Empty ? null : option.Member.Id;
        _selected = _today;
        UpdateMemberActions();
        HistoryPicker.Visibility = Visibility.Collapsed;
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
    }

    private async void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || HistoryPicker.SelectedItem is not HistoryOption option) return;
        _selected = option.Date;
        await ReloadAsync();
    }

    private async void AddMember_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FamilyInviteWindow(_api) { Owner = this };
        dialog.ShowDialog();
        await LoadFamilyAsync();
    }

    private void UpdateMemberActions()
    {
        var active = MemberPicker.SelectedItem is MemberOption { Member: { Archived: false } } option
            && option.Member.Id != Guid.Empty;
        ArchiveMemberButton.IsEnabled = active;
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

    private async void ArchiveMember_Click(object sender, RoutedEventArgs e)
    {
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

    private sealed record HistoryOption(DateOnly Date, string Label);

    private sealed record MemberOption(FamilyMember Member)
    {
        public string Label => Member.Archived ? $"{Member.Name}（已停用）" : Member.Name;
    }

    private async void PrevDay_Click(object sender, RoutedEventArgs e)
    {
        _selected = _selected.AddDays(-1);
        await ReloadAsync();
    }

    private async void NextDay_Click(object sender, RoutedEventArgs e)
    {
        if (_selected >= _today) return;
        _selected = _selected.AddDays(1);
        await ReloadAsync();
    }

    private static RecordItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as RecordItem;

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_records is not { Editable: true }) return;
        if (ItemOf(sender) is not { } item) return;
        var dialog = new AmountWindow(initial: Math.Abs(item.Event.AmountMl), cup: _cupMl, title: "修改记录", okText: "保存") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var amount = item.Event.AmountMl < 0 ? -dialog.AmountMl : dialog.AmountMl;
        var saved = await _api.EditRecordAsync(item.Event.Id, amount);
        if (!saved.Ok) { SummaryText.Text = saved.Error ?? "没能修改"; return; }
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_records is not { Editable: true }) return;
        if (ItemOf(sender) is not { } item) return;
        if (!ConfirmDialog.Ask(this, "删除这条记录？", $"{item.TimeText} 的 {item.AmountDisplay} 将被删除。", "删除", danger: true)) return;
        var saved = await _api.DeleteRecordAsync(item.Event.Id);
        if (!saved.Ok) { SummaryText.Text = saved.Error ?? "没能删除"; return; }
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_records is not { Editable: true } || _records.Events.Count == 0) return;
        if (!ConfirmDialog.Ask(this, "清空今天的所有记录？", "今天的饮水进度将归零，此操作无法撤销。", "清空", danger: true)) return;
        var saved = await _api.ClearTodayAsync();
        if (!saved.Ok) { SummaryText.Text = saved.Error ?? "没能清空"; return; }
        await Task.WhenAll(ReloadAsync(), LoadHistoryAsync());
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
