using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WaterReminder;
using WaterReminder.Client.Controls;
using WaterReminder.Services;

namespace WaterReminder.Client;

/// <summary>The original records window, reading and editing today's events on the server.</summary>
public partial class RecordsWindow : Window
{
    private readonly ReminderApiClient _api;
    private readonly int _cupMl;
    private TodayRecordsResponse? _records;

    public RecordsWindow(ReminderApiClient api, int cupMl)
    {
        ClientTheme.Ensure();
        InitializeComponent();
        _api = api;
        _cupMl = cupMl;
        WindowEx.EnableDrag(this);
        WpfInterop.EnableKeyboard(this);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var result = await _api.GetRecordsAsync();
        if (!result.Ok || result.Value is null)
        {
            SummaryText.Text = result.Error ?? "无法读取记录";
            return;
        }
        _records = result.Value;
        var events = result.Value.Events
            .OrderByDescending(x => x.At)
            .Select(x => new RecordItem(x))
            .ToList();
        RecordsList.ItemsSource = events;
        var count = events.Count;
        var date = DateTime.TryParse(result.Value.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("M月d日 dddd")
            : result.Value.Date;
        SummaryText.Text = count == 0
            ? date
            : $"共 {count} 条 · 合计 {result.Value.ConsumedMl:N0} / {result.Value.GoalMl:N0} ml";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = count > 0;
    }

    private static RecordItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as RecordItem;

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var dialog = new AmountWindow(initial: Math.Abs(item.Event.AmountMl), cup: _cupMl, title: "修改记录", okText: "保存") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var amount = item.Event.AmountMl < 0 ? -dialog.AmountMl : dialog.AmountMl;
        var saved = await _api.EditRecordAsync(item.Event.Id, amount);
        if (!saved.Ok) SummaryText.Text = saved.Error ?? "没能修改";
        await ReloadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!ConfirmDialog.Ask(this, "删除这条记录？", $"{item.TimeText} 的 {item.AmountDisplay} 将被删除。", "删除", danger: true)) return;
        var saved = await _api.DeleteRecordAsync(item.Event.Id);
        if (!saved.Ok) SummaryText.Text = saved.Error ?? "没能删除";
        await ReloadAsync();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_records is null || _records.Events.Count == 0) return;
        if (!ConfirmDialog.Ask(this, "清空今天的所有记录？", "今天的饮水进度将归零，此操作无法撤销。", "清空", danger: true)) return;
        var saved = await _api.ClearTodayAsync();
        if (!saved.Ok) SummaryText.Text = saved.Error ?? "没能清空";
        await ReloadAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    private sealed class RecordItem
    {
        public RecordEventResponse Event { get; }
        public RecordItem(RecordEventResponse @event) => Event = @event;
        public string AmountDisplay => Event.AmountMl >= 0 ? $"+{Event.AmountMl} ml" : $"{Event.AmountMl} ml";
        public string Glyph => Event.Source == "adjust" ? "\uE70F" : Event.AmountMl < 0 ? "\uE738" : Event.Source == "custom" ? "\uE70F" : "\uE710";
        public string TimeText => DateTime.TryParse(Event.At, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString("HH:mm") : Event.At;
        public string Detail => $"{TimeText} · {RecordLabels.Describe(Event.Source, Event.AmountMl)}";
    }
}
