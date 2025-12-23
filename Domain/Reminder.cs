namespace PillsReminderBot.Domain;

public sealed class Reminder
{
    public long Id { get; set; }

    public long TelegramUserId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public ReminderType Type { get; set; }

    /// <summary>
    /// For DailyAtTime: minutes from 00:00 (0..1439).
    /// </summary>
    public int? DailyTimeMinutes { get; set; }

    /// <summary>
    /// For EveryNMinutesInWindow: window start in minutes from 00:00 (0..1439).
    /// </summary>
    public int? WindowStartMinutes { get; set; }

    /// <summary>
    /// For EveryNMinutesInWindow: window end in minutes from 00:00 (0..1439), must be > WindowStartMinutes.
    /// </summary>
    public int? WindowEndMinutes { get; set; }

    /// <summary>
    /// For EveryNMinutesInWindow: interval in minutes (e.g. 360 for every 6 hours).
    /// </summary>
    public int? EveryMinutes { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, reminder is actively asking for confirmation; scheduler repeats every 2 hours until ack.
    /// </summary>
    public bool AwaitingAck { get; set; }

    /// <summary>
    /// Id of current "confirmation cycle" to prevent stale callbacks acknowledging a newer cycle.
    /// </summary>
    public string? ActiveCycleId { get; set; }

    public DateTimeOffset NextFireAtUtc { get; set; }
    public DateTimeOffset? LastFiredAtUtc { get; set; }
    public DateTimeOffset? LastAcknowledgedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}


