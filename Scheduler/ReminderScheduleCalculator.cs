using PillsReminderBot.Domain;

namespace PillsReminderBot.Scheduler;

public sealed class ReminderScheduleCalculator
{
    public DateTimeOffset CalculateNextFireAtUtc(Reminder reminder, string? timeZoneId, DateTimeOffset nowUtc)
    {
        var offset = ParseUtcOffsetOrZero(timeZoneId);
        var nowLocal = nowUtc.ToOffset(offset);

        return reminder.Type switch
        {
            ReminderType.DailyAtTime when reminder.DailyTimeMinutes is int dailyMinutes
                => CalculateNextDailyLocal(nowLocal, offset, dailyMinutes).ToUniversalTime(),
            ReminderType.EveryNMinutesInWindow
                when reminder.WindowStartMinutes is int windowStart
                     && reminder.WindowEndMinutes is int windowEnd
                     && reminder.EveryMinutes is int everyMinutes
                => CalculateNextInWindowLocal(nowLocal, offset, windowStart, windowEnd, everyMinutes).ToUniversalTime(),
            _ => nowUtc.AddDays(1)
        };
    }

    public static TimeSpan ParseUtcOffsetOrZero(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeSpan.Zero;

        if (!timeZoneId.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.Zero;

        var rest = timeZoneId["UTC".Length..].Trim();
        if (rest.Length == 0)
            return TimeSpan.Zero;

        var sign = rest[0];
        if (sign != '+' && sign != '-')
            return TimeSpan.Zero;

        rest = rest[1..];
        var parts = rest.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return TimeSpan.Zero;
        if (!int.TryParse(parts[0], out var hh)) return TimeSpan.Zero;
        if (!int.TryParse(parts[1], out var mm)) return TimeSpan.Zero;
        if (hh is < 0 or > 14) return TimeSpan.Zero;
        if (mm is not (0 or 15 or 30 or 45)) return TimeSpan.Zero;

        var offset = new TimeSpan(hh, mm, 0);
        return sign == '-' ? -offset : offset;
    }

    private static DateTimeOffset CalculateNextDailyLocal(DateTimeOffset nowLocal, TimeSpan offset, int dailyMinutes)
    {
        var targetTodayLocal = new DateTimeOffset(
            year: nowLocal.Year,
            month: nowLocal.Month,
            day: nowLocal.Day,
            hour: dailyMinutes / 60,
            minute: dailyMinutes % 60,
            second: 0,
            offset: offset);

        return targetTodayLocal > nowLocal ? targetTodayLocal : targetTodayLocal.AddDays(1);
    }

    private static DateTimeOffset CalculateNextInWindowLocal(
        DateTimeOffset nowLocal,
        TimeSpan offset,
        int windowStart,
        int windowEnd,
        int everyMinutes)
    {
        var dayStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, offset);
        var windowStartLocal = dayStart.AddMinutes(windowStart);
        var windowEndLocal = dayStart.AddMinutes(windowEnd);

        if (nowLocal < windowStartLocal)
            return windowStartLocal;

        if (nowLocal >= windowEndLocal)
            return windowStartLocal.AddDays(1);

        var minutesSinceStart = (nowLocal - windowStartLocal).TotalMinutes;
        var intervalIndex = (int)Math.Floor(minutesSinceStart / everyMinutes);
        var candidate = windowStartLocal.AddMinutes(intervalIndex * everyMinutes);
        if (candidate <= nowLocal)
            candidate = candidate.AddMinutes(everyMinutes);

        return candidate < windowEndLocal ? candidate : windowStartLocal.AddDays(1);
    }
}
