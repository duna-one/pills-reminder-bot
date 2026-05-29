using PillsReminderBot.Domain;

namespace PillsReminderBot.Tests;

public sealed class WeekDayMaskTests
{
    [Fact]
    public void Format_AllDays_ReturnsDaily()
    {
        Assert.Equal("ежедневно", WeekDayMask.Format(WeekDayMask.AllDays));
    }

    [Fact]
    public void Format_Weekdays_ReturnsWeekdays()
    {
        Assert.Equal("будни", WeekDayMask.Format(WeekDayMask.Weekdays));
    }

    [Fact]
    public void Format_CustomDays_ReturnsShortNames()
    {
        var mask = WeekDayMask.Monday | WeekDayMask.Wednesday | WeekDayMask.Friday;

        Assert.Equal("Пн, Ср, Пт", WeekDayMask.Format(mask));
    }

    [Fact]
    public void FormatSelection_NoDays_ReturnsNotSelected()
    {
        Assert.Equal("не выбрано", WeekDayMask.FormatSelection(0));
    }

    [Fact]
    public void Toggle_ExistingDay_RemovesDay()
    {
        var result = WeekDayMask.Toggle(WeekDayMask.AllDays, WeekDayMask.Wednesday);

        Assert.False(WeekDayMask.Contains(result, DayOfWeek.Wednesday));
        Assert.True(WeekDayMask.Contains(result, DayOfWeek.Thursday));
    }

    [Fact]
    public void Toggle_MissingDay_AddsDay()
    {
        var result = WeekDayMask.Toggle(WeekDayMask.Weekdays, WeekDayMask.Saturday);

        Assert.True(WeekDayMask.Contains(result, DayOfWeek.Saturday));
    }
}
