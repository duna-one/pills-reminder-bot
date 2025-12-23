namespace PillsReminderBot.Domain;

public sealed class UserProfile
{
    public long Id { get; set; }

    public long TelegramUserId { get; set; }
    public long ChatId { get; set; }

    /// <summary>
    /// IANA timezone id (preferred), e.g. "Europe/Moscow".
    /// If user selected fixed offset, can be stored as "UTC+03:00".
    /// </summary>
    public string? TimeZoneId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}


