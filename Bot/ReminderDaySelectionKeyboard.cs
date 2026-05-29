using PillsReminderBot.Domain;
using Telegram.Bot.Types.ReplyMarkups;

namespace PillsReminderBot.Bot;

public static class ReminderDaySelectionKeyboard
{
    public const string TogglePrefix = "day_toggle:";
    public const string AllCallback = "days_all";
    public const string WeekdaysCallback = "days_weekdays";
    public const string OkCallback = "days_ok";

    public static InlineKeyboardMarkup Build(int selectedMask)
    {
        var dayButtons = WeekDayMask.OrderedDays
            .Select(d =>
            {
                var selected = (selectedMask & d.Bit) != 0;
                var text = selected ? $"✓ {d.ShortName}" : d.ShortName;
                return InlineKeyboardButton.WithCallbackData(text, $"{TogglePrefix}{d.Bit}");
            })
            .ToArray();

        return new InlineKeyboardMarkup(new[]
        {
            dayButtons,
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Ежедневно", AllCallback),
                InlineKeyboardButton.WithCallbackData("Будни", WeekdaysCallback),
                InlineKeyboardButton.WithCallbackData("OK", OkCallback)
            }
        });
    }
}
