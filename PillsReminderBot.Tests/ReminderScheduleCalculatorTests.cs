using PillsReminderBot.Domain;
using PillsReminderBot.Scheduler;

namespace PillsReminderBot.Tests;

public sealed class ReminderScheduleCalculatorTests
{
    private readonly ReminderScheduleCalculator _calculator = new();

    [Fact]
    public void CalculateNextFireAtUtc_DailyTimeLaterToday_ReturnsToday()
    {
        var reminder = new Reminder
        {
            Type = ReminderType.DailyAtTime,
            DailyTimeMinutes = 9 * 60
        };
        var nowUtc = new DateTimeOffset(2026, 5, 27, 5, 30, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 27, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_DailyTimeAlreadyPassed_ReturnsTomorrow()
    {
        var reminder = new Reminder
        {
            Type = ReminderType.DailyAtTime,
            DailyTimeMinutes = 9 * 60
        };
        var nowUtc = new DateTimeOffset(2026, 5, 27, 7, 0, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 28, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_DailyTimeTodayNotSelected_ReturnsNextSelectedDay()
    {
        var reminder = new Reminder
        {
            Type = ReminderType.DailyAtTime,
            DailyTimeMinutes = 9 * 60,
            WeekDaysMask = WeekDayMask.Friday
        };
        var nowUtc = new DateTimeOffset(2026, 5, 27, 5, 30, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 29, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_DailyTimeAlreadyPassed_ReturnsNextSelectedDay()
    {
        var reminder = new Reminder
        {
            Type = ReminderType.DailyAtTime,
            DailyTimeMinutes = 9 * 60,
            WeekDaysMask = WeekDayMask.Monday | WeekDayMask.Wednesday
        };
        var nowUtc = new DateTimeOffset(2026, 5, 27, 7, 0, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_WindowBeforeStart_ReturnsWindowStartToday()
    {
        var reminder = WindowReminder();
        var nowUtc = new DateTimeOffset(2026, 5, 27, 5, 0, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 27, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_WindowInside_ReturnsNextInterval()
    {
        var reminder = WindowReminder();
        var nowUtc = new DateTimeOffset(2026, 5, 27, 7, 10, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_WindowAtEnd_ReturnsWindowStartTomorrow()
    {
        var reminder = WindowReminder();
        var nowUtc = new DateTimeOffset(2026, 5, 27, 18, 0, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 28, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void CalculateNextFireAtUtc_WindowAfterEnd_ReturnsWindowStartTomorrow()
    {
        var reminder = WindowReminder();
        var nowUtc = new DateTimeOffset(2026, 5, 27, 19, 30, 0, TimeSpan.Zero);

        var result = _calculator.CalculateNextFireAtUtc(reminder, "UTC+03:00", nowUtc);

        Assert.Equal(new DateTimeOffset(2026, 5, 28, 6, 0, 0, TimeSpan.Zero), result);
    }

    [Theory]
    [InlineData("UTC+03:00", 3, 0)]
    [InlineData("UTC-01:00", -1, 0)]
    [InlineData("", 0, 0)]
    [InlineData(null, 0, 0)]
    public void ParseUtcOffsetOrZero_ReturnsExpectedOffset(string? timeZoneId, int hours, int minutes)
    {
        var result = ReminderScheduleCalculator.ParseUtcOffsetOrZero(timeZoneId);

        Assert.Equal(new TimeSpan(hours, minutes, 0), result);
    }

    private static Reminder WindowReminder()
        => new()
        {
            Type = ReminderType.EveryNMinutesInWindow,
            WindowStartMinutes = 9 * 60,
            WindowEndMinutes = 21 * 60,
            EveryMinutes = 180
        };
}
