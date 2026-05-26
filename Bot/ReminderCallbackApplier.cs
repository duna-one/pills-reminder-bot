using PillsReminderBot.Domain;

namespace PillsReminderBot.Bot;

public static class ReminderCallbackApplier
{
    public static bool TrySnooze(Reminder reminder, string cycleId, DateTimeOffset nowUtc, int snoozeMinutes)
    {
        if (!string.Equals(reminder.ActiveCycleId, cycleId, StringComparison.Ordinal))
            return false;

        reminder.AwaitingAck = true;
        reminder.NextFireAtUtc = nowUtc.AddMinutes(snoozeMinutes);
        reminder.UpdatedAtUtc = nowUtc;
        return true;
    }
}
