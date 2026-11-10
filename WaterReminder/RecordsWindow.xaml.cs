using System.Windows;
using System.Windows.Controls;
using WaterReminder.Controls;
using WaterReminder.Services;

namespace WaterReminder;

public partial class RecordsWindow : Window
{
    private readonly AppServices _services;

    public RecordsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        WindowEx.EnableDrag(this);
        Reload();
    }

    private void Reload()
    {
        _services.RefreshToday();
        var events = _services.Today.Events;
        RecordsList.ItemsSource = events.OrderByDescending(x => x.At).Select(x => new RecordItem(x)).ToList();
        var count = events.Count;
        SummaryText.Text = count == 0 ? DateTime.Now.ToString("M月d日 dddd")
            : $"共 {count} 条 · 合计 {_services.Today.ConsumedMl:N0} / {_services.Settings.DailyGoalMl:N0} ml";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordsList.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = count > 0;
    }

    private static RecordItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as RecordItem;

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var cup = WaterCalculator.CurrentCupMl(_services.Settings, DateTime.Now);
        var dialog = new AmountWindow(initial: item.Event.AmountMl, cup: cup, title: "修改记录", okText: "保存") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _services.Progress.EditEvent(item.Event.Id, item.Event.AmountMl < 0 ? -dialog.AmountMl : dialog.AmountMl);
        Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!ConfirmDialog.Ask(this, "删除这条记录？", $"{item.Event.At:HH:mm} 的 {item.AmountDisplay} 将被删除。", "删除", danger: true)) return;
        _services.Progress.DeleteEvent(item.Event.Id);
        Reload();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Today.Events.Count == 0) return;
        if (!ConfirmDialog.Ask(this, "清空今天的所有记录？", "今天的饮水进度将归零，此操作无法撤销。", "清空", danger: true)) return;
        _services.Progress.ClearToday();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }

    private sealed class RecordItem
    {
        public WaterEvent Event { get; }
        public RecordItem(WaterEvent @event) => Event = @event;
        public string AmountDisplay => Event.AmountMl >= 0 ? $"+{Event.AmountMl} ml" : $"{Event.AmountMl} ml";
        public string Glyph => Event.Source == "adjust" ? "\uE70F" : Event.AmountMl < 0 ? "\uE738" : Event.Source == "custom" ? "\uE70F" : "\uE710";
        public string Detail => $"{Event.At:HH:mm} · " + RecordLabels.Describe(Event.Source, Event.AmountMl);
    }
}
