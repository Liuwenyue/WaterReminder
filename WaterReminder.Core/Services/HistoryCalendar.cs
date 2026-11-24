using WaterReminder;

namespace WaterReminder.Services;

public readonly record struct CalendarCell(
    DateOnly? Date,
    string Label,
    int ConsumedMl,
    bool InPeriod,
    bool Future,
    bool Selected,
    double Fraction);

public static class HistoryCalendar
{
    public static DateOnly StartOfWeek(DateOnly date)
        => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static IReadOnlyList<CalendarCell> Week(DateOnly selected, DateOnly today, IReadOnlyDictionary<DateOnly, HistoryDayResponse> history)
    {
        var start = StartOfWeek(selected);
        var days = Enumerable.Range(0, 7).Select(start.AddDays).ToList();
        var ceiling = Ceiling(days, selected, today, history);
        return days.Select(day => Cell(day, day.Day.ToString(), selected, today, history, ceiling)).ToList();
    }

    public static IReadOnlyList<CalendarCell> Month(DateOnly selected, DateOnly today, IReadOnlyDictionary<DateOnly, HistoryDayResponse> history)
    {
        var first = new DateOnly(selected.Year, selected.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var count = DateTime.DaysInMonth(selected.Year, selected.Month);
        var days = Enumerable.Range(1, count).Select(day => new DateOnly(selected.Year, selected.Month, day)).ToList();
        var ceiling = Ceiling(days, selected, today, history);
        var cells = new List<CalendarCell>();
        for (var index = 0; index < lead; index++) cells.Add(new(null, "", 0, false, false, false, 0));
        cells.AddRange(days.Select(day => Cell(day, day.Day.ToString(), selected, today, history, ceiling)));
        return cells;
    }

    public static (int Consumed, int Achieved, int Recorded) Summary(IEnumerable<DateOnly> days, DateOnly today, IReadOnlyDictionary<DateOnly, HistoryDayResponse> history)
    {
        var consumed = 0;
        var achieved = 0;
        var recorded = 0;
        foreach (var day in days)
        {
            if (day > today || !history.TryGetValue(day, out var row)) continue;
            recorded++;
            consumed += row.ConsumedMl;
            if (row.GoalKnown && row.GoalMl > 0 && row.ConsumedMl >= row.GoalMl) achieved++;
        }
        return (consumed, achieved, recorded);
    }

    private static int Ceiling(IEnumerable<DateOnly> days, DateOnly selected, DateOnly today, IReadOnlyDictionary<DateOnly, HistoryDayResponse> history)
    {
        var ceiling = 1;
        if (history.TryGetValue(selected, out var selectedRow) && selectedRow.GoalKnown) ceiling = Math.Max(ceiling, selectedRow.GoalMl);
        else if (history.TryGetValue(today, out var todayRow) && todayRow.GoalKnown) ceiling = Math.Max(ceiling, todayRow.GoalMl);
        foreach (var day in days)
            if (history.TryGetValue(day, out var row)) ceiling = Math.Max(ceiling, row.ConsumedMl);
        return ceiling;
    }

    private static CalendarCell Cell(DateOnly date, string label, DateOnly selected, DateOnly today, IReadOnlyDictionary<DateOnly, HistoryDayResponse> history, int ceiling)
    {
        history.TryGetValue(date, out var row);
        var consumed = row?.ConsumedMl ?? 0;
        return new(date, label, consumed, true, date > today, date == selected, consumed / (double)ceiling);
    }
}
