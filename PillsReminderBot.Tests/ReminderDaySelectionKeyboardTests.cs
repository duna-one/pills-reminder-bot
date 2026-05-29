using PillsReminderBot.Bot;
using PillsReminderBot.Domain;

namespace PillsReminderBot.Tests;

public sealed class ReminderDaySelectionKeyboardTests
{
    [Fact]
    public void Build_SelectedDaysHaveCheckMarks()
    {
        var keyboard = ReminderDaySelectionKeyboard.Build(WeekDayMask.Monday | WeekDayMask.Wednesday);

        var rows = keyboard.InlineKeyboard.ToArray();
        var dayButtons = rows[0].ToArray();

        Assert.Equal("✓ Пн", dayButtons[0].Text);
        Assert.Equal("Вт", dayButtons[1].Text);
        Assert.Equal("✓ Ср", dayButtons[2].Text);
    }

    [Fact]
    public void Build_ActionButtonsAreInBottomRow()
    {
        var keyboard = ReminderDaySelectionKeyboard.Build(WeekDayMask.AllDays);

        var rows = keyboard.InlineKeyboard.ToArray();
        var actionButtons = rows[1].ToArray();

        Assert.Equal("Ежедневно", actionButtons[0].Text);
        Assert.Equal("Будни", actionButtons[1].Text);
        Assert.Equal("OK", actionButtons[2].Text);
    }
}
