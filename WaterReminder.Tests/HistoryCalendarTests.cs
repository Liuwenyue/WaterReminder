using Xunit;

using WaterReminder.Services;

namespace WaterReminder.Tests;

public sealed class HistoryCalendarTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);

    [Fact]
    public void WeekStartsOnMondayAndMarksTheSelectedDay()
    {
        var history = new Dictionary<DateOnly, HistoryDayResponse>
        {
            [Today] = new() { Date = "2026-11-24", ConsumedMl = 800, GoalMl = 2000, GoalKnown = true, EventCount = 2 }
        };
        var week = HistoryCalendar.Week(Today, Today, history);
        Assert.Equal(new DateOnly(2026, 11, 23), week[0].Date);
        Assert.Equal(new DateOnly(2026, 11, 29), week[6].Date);
        Assert.Equal(800, Assert.Single(week, day => day.Selected).ConsumedMl);
        Assert.True(week[6].Future);
    }

    [Fact]
    public void MonthPadsUntilMondayAndSummarizesOnlyRecordedDays()
    {
        var history = new Dictionary<DateOnly, HistoryDayResponse>
        {
            [new DateOnly(2026, 11, 2)] = new() { ConsumedMl = 2000, GoalMl = 2000, GoalKnown = true, EventCount = 1 },
            [Today] = new() { ConsumedMl = 500, GoalMl = 2000, GoalKnown = true, EventCount = 1 }
        };
        var month = HistoryCalendar.Month(Today, Today, history);
        Assert.All(month.Take(6), cell => Assert.Null(cell.Date));
        Assert.Equal(new DateOnly(2026, 11, 1), month[6].Date);
        var days = Enumerable.Range(1, 30).Select(day => new DateOnly(2026, 11, day));
        Assert.Equal((2500, 1, 2), HistoryCalendar.Summary(days, Today, history));
    }
}
