namespace PillsReminderBot.Bot;

public enum ReminderCallbackAction
{
    Acknowledge = 1,
    Snooze = 2
}

public sealed record ReminderCallbackData(
    ReminderCallbackAction Action,
    long ReminderId,
    string CycleId,
    int? SnoozeMinutes)
{
    public static bool TryParse(string? payload, out ReminderCallbackData? data)
    {
        data = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        if (payload.StartsWith("ack:", StringComparison.Ordinal))
        {
            var parts = payload["ack:".Length..].Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var reminderId) || string.IsNullOrWhiteSpace(parts[1]))
                return false;

            data = new ReminderCallbackData(ReminderCallbackAction.Acknowledge, reminderId, parts[1], null);
            return true;
        }

        if (payload.StartsWith("snooze:", StringComparison.Ordinal))
        {
            var parts = payload["snooze:".Length..].Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3
                || !long.TryParse(parts[0], out var reminderId)
                || string.IsNullOrWhiteSpace(parts[1])
                || !int.TryParse(parts[2], out var snoozeMinutes)
                || snoozeMinutes <= 0)
            {
                return false;
            }

            data = new ReminderCallbackData(ReminderCallbackAction.Snooze, reminderId, parts[1], snoozeMinutes);
            return true;
        }

        return false;
    }
}
