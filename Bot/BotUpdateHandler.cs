using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using PillsReminderBot.Domain;
using PillsReminderBot.Persistence;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PillsReminderBot.Bot;

public sealed class BotUpdateHandler
{
    private readonly ILogger<BotUpdateHandler> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public BotUpdateHandler(
        ILogger<BotUpdateHandler> logger,
        ITelegramBotClient bot,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _bot = bot;
        _dbFactory = dbFactory;
    }

    public async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        switch (update.Type)
        {
            case UpdateType.Message when update.Message is not null:
                await HandleMessageAsync(update.Message, ct);
                break;
            case UpdateType.CallbackQuery when update.CallbackQuery is not null:
                await HandleCallbackQueryAsync(update.CallbackQuery, ct);
                break;
        }
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        if (message.Type != MessageType.Text)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From?.Id;
        var text = message.Text ?? string.Empty;

        _logger.LogInformation("Message from chatId={ChatId}: {Text}", chatId, text);

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await UpsertUserProfileAsync(userId.Value, chatId, ct);

            await _bot.SendMessage(
                chatId: chatId,
                text: "Привет! Я бот-напоминалка.\n\nСначала выбери часовой пояс командой /timezone.",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/timezone", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await UpsertUserProfileAsync(userId.Value, chatId, ct);

            await _bot.SendMessage(
                chatId: chatId,
                text: "Выбери свой UTC-сдвиг:",
                replyMarkup: BuildTimeZoneKeyboard(),
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            // MVP: /new HH:mm Текст
            // Example: /new 09:30 Витамин D
            var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                await _bot.SendMessage(
                    chatId,
                    "Формат: /new HH:mm Текст\nПример: /new 09:30 Витамин D",
                    cancellationToken: ct);
                return;
            }

            if (!TryParseTime(parts[1], out var minutes))
            {
                await _bot.SendMessage(chatId, "Неверное время. Формат: HH:mm (например 09:30).", cancellationToken: ct);
                return;
            }

            await UpsertUserProfileAsync(userId.Value, chatId, ct);

            var now = DateTimeOffset.UtcNow;
            var nextFireAtUtc = await CalculateNextFireAtUtc(
                telegramUserId: userId.Value,
                type: ReminderType.DailyAtTime,
                dailyMinutes: minutes,
                windowStartMinutes: null,
                windowEndMinutes: null,
                everyMinutes: null,
                nowUtc: now,
                ct: ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = new Reminder
            {
                TelegramUserId = userId.Value,
                Title = parts[2],
                Message = parts[2],
                Type = ReminderType.DailyAtTime,
                DailyTimeMinutes = minutes,
                NextFireAtUtc = nextFireAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            var nextLocalText = await FormatLocalAsync(nextFireAtUtc, userId.Value, ct);
            await _bot.SendMessage(
                chatId: chatId,
                text: $"Создано напоминание #{reminder.Id}: каждый день в {parts[1]}.\nСледующий раз: {nextLocalText}",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/newi", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            // MVP: /newi 09:00 21:00 360 Текст
            // where 360 = every 360 minutes (6h)
            var parts = text.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
            {
                await _bot.SendMessage(
                    chatId,
                    "Формат: /newi HH:mm HH:mm <каждые_минут> Текст\nПример: /newi 09:00 21:00 360 Витамин D",
                    cancellationToken: ct);
                return;
            }

            if (!TryParseTime(parts[1], out var startMinutes) || !TryParseTime(parts[2], out var endMinutes))
            {
                await _bot.SendMessage(chatId, "Неверное время. Формат: HH:mm (например 09:00).", cancellationToken: ct);
                return;
            }

            if (endMinutes <= startMinutes)
            {
                await _bot.SendMessage(chatId, "Конец интервала должен быть больше начала.", cancellationToken: ct);
                return;
            }

            if (!int.TryParse(parts[3], out var everyMinutes) || everyMinutes <= 0)
            {
                await _bot.SendMessage(chatId, "Неверный интервал. Укажи число минут > 0 (например 360).", cancellationToken: ct);
                return;
            }

            if (everyMinutes < 30)
            {
                await _bot.SendMessage(chatId, "Слишком часто. Минимум: 30 минут.", cancellationToken: ct);
                return;
            }

            await UpsertUserProfileAsync(userId.Value, chatId, ct);

            var now = DateTimeOffset.UtcNow;
            var nextFireAtUtc = await CalculateNextFireAtUtc(
                telegramUserId: userId.Value,
                type: ReminderType.EveryNMinutesInWindow,
                dailyMinutes: null,
                windowStartMinutes: startMinutes,
                windowEndMinutes: endMinutes,
                everyMinutes: everyMinutes,
                nowUtc: now,
                ct: ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = new Reminder
            {
                TelegramUserId = userId.Value,
                Title = parts[4],
                Message = parts[4],
                Type = ReminderType.EveryNMinutesInWindow,
                WindowStartMinutes = startMinutes,
                WindowEndMinutes = endMinutes,
                EveryMinutes = everyMinutes,
                NextFireAtUtc = nextFireAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            var nextLocalText = await FormatLocalAsync(nextFireAtUtc, userId.Value, ct);
            await _bot.SendMessage(
                chatId: chatId,
                text: $"Создано напоминание #{reminder.Id}: {parts[1]}–{parts[2]} каждые {everyMinutes} мин.\nСледующий раз: {nextLocalText}",
                cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var tzId = await db.UserProfiles
                .Where(p => p.TelegramUserId == userId.Value)
                .Select(p => p.TimeZoneId)
                .SingleOrDefaultAsync(ct);
            var offset = ParseUtcOffsetOrZero(tzId);

            var items = await db.Reminders
                .Where(r => r.TelegramUserId == userId.Value)
                .OrderBy(r => r.Id)
                .Select(r => new
                {
                    r.Id,
                    r.Title,
                    r.IsEnabled,
                    r.AwaitingAck,
                    r.NextFireAtUtc,
                    r.Type,
                    r.DailyTimeMinutes,
                    r.WindowStartMinutes,
                    r.WindowEndMinutes,
                    r.EveryMinutes
                })
                .ToListAsync(ct);

            if (items.Count == 0)
            {
                await _bot.SendMessage(chatId, "Напоминаний пока нет. Создай: /new HH:mm Текст", cancellationToken: ct);
                return;
            }

            var lines = items.Select(i =>
            {
                var schedule = i.Type switch
                {
                    ReminderType.DailyAtTime when i.DailyTimeMinutes is int dm
                        => $"{dm / 60:D2}:{dm % 60:D2}",
                    ReminderType.EveryNMinutesInWindow when i.WindowStartMinutes is int ws && i.WindowEndMinutes is int we && i.EveryMinutes is int ev
                        => $"{ws / 60:D2}:{ws % 60:D2}–{we / 60:D2}:{we % 60:D2} / {ev}m",
                    _ => "—"
                };
                var status = i.IsEnabled ? "on" : "off";
                var ack = i.AwaitingAck ? " (ждёт ✅)" : string.Empty;
                var nextLocal = i.NextFireAtUtc.ToOffset(offset);
                return $"#{i.Id} [{status}]{ack} {schedule} — {i.Title} | next: {nextLocal:yyyy-MM-dd HH:mm} ({offset:hh\\:mm})";
            });

            await _bot.SendMessage(chatId, string.Join("\n", lines), cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/delete", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
            {
                await _bot.SendMessage(chatId, "Формат: /delete <id>", cancellationToken: ct);
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId.Value, ct);
            if (reminder is null)
            {
                await _bot.SendMessage(chatId, $"Не найдено напоминание #{id}.", cancellationToken: ct);
                return;
            }

            db.Reminders.Remove(reminder);
            await db.SaveChangesAsync(ct);
            await _bot.SendMessage(chatId, $"Удалено напоминание #{id}.", cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/disable", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/enable", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            var enable = text.StartsWith("/enable", StringComparison.OrdinalIgnoreCase);
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
            {
                await _bot.SendMessage(chatId, $"Формат: {(enable ? "/enable" : "/disable")} <id>", cancellationToken: ct);
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId.Value, ct);
            if (reminder is null)
            {
                await _bot.SendMessage(chatId, $"Не найдено напоминание #{id}.", cancellationToken: ct);
                return;
            }

            reminder.IsEnabled = enable;
            reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _bot.SendMessage(chatId, $"Ок. Напоминание #{id} {(enable ? "включено" : "выключено")}.", cancellationToken: ct);
            return;
        }

        // Пока что: echo, чтобы было удобно проверять, что бот “жив”.
        await _bot.SendMessage(chatId, $"Вы написали: {text}", cancellationToken: ct);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Data is null)
            return;

        if (cq.From is null)
            return;

        if (cq.Data.StartsWith("tz:", StringComparison.Ordinal))
        {
            var tz = cq.Data["tz:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(tz))
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var profile = await db.UserProfiles.SingleOrDefaultAsync(x => x.TelegramUserId == cq.From.Id, ct);
            if (profile is null)
            {
                profile = new UserProfile
                {
                    TelegramUserId = cq.From.Id,
                    ChatId = cq.Message?.Chat.Id ?? 0,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                db.UserProfiles.Add(profile);
            }

            if (cq.Message is not null)
                profile.ChatId = cq.Message.Chat.Id;

            profile.TimeZoneId = tz;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _bot.AnswerCallbackQuery(cq.Id, text: $"Часовой пояс сохранён: {tz}", cancellationToken: ct);

            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: $"Ок! Сохранил часовой пояс: {tz}",
                    cancellationToken: ct);
            }

            return;
        }

        // Expected: "ack:<reminderId>:<cycleId>"
        if (cq.Data.StartsWith("ack:", StringComparison.Ordinal))
        {
            var payload = cq.Data["ack:".Length..];
            var parts = payload.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var reminderId))
                return;
            var cycleId = parts[1];
            if (string.IsNullOrWhiteSpace(cycleId))
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(
                r => r.Id == reminderId && r.TelegramUserId == cq.From.Id,
                ct);

            if (reminder is null)
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Напоминание не найдено.", cancellationToken: ct);
                return;
            }

            if (!string.Equals(reminder.ActiveCycleId, cycleId, StringComparison.Ordinal))
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Это подтверждение уже не актуально.", cancellationToken: ct);
                return;
            }

            reminder.AwaitingAck = false;
            reminder.ActiveCycleId = null;
            reminder.LastAcknowledgedAtUtc = DateTimeOffset.UtcNow;
            reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;

            if (reminder.Type == ReminderType.DailyAtTime && reminder.DailyTimeMinutes is int mins)
            {
                reminder.NextFireAtUtc = await CalculateNextFireAtUtc(
                    telegramUserId: cq.From.Id,
                    type: reminder.Type,
                    dailyMinutes: reminder.DailyTimeMinutes,
                    windowStartMinutes: reminder.WindowStartMinutes,
                    windowEndMinutes: reminder.WindowEndMinutes,
                    everyMinutes: reminder.EveryMinutes,
                    nowUtc: DateTimeOffset.UtcNow,
                    ct: ct);
            }
            else if (reminder.Type == ReminderType.EveryNMinutesInWindow)
            {
                reminder.NextFireAtUtc = await CalculateNextFireAtUtc(
                    telegramUserId: cq.From.Id,
                    type: reminder.Type,
                    dailyMinutes: reminder.DailyTimeMinutes,
                    windowStartMinutes: reminder.WindowStartMinutes,
                    windowEndMinutes: reminder.WindowEndMinutes,
                    everyMinutes: reminder.EveryMinutes,
                    nowUtc: DateTimeOffset.UtcNow,
                    ct: ct);
            }
            else
            {
                // fallback: postpone 1 day
                reminder.NextFireAtUtc = DateTimeOffset.UtcNow.AddDays(1);
            }

            await db.SaveChangesAsync(ct);
            await _bot.AnswerCallbackQuery(cq.Id, text: "Отлично! Отметил как выпито ✅", cancellationToken: ct);

            if (cq.Message is not null)
            {
                var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, cq.From.Id, ct);
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: $"✅ Принято. Следующее напоминание: {nextLocalText}",
                    cancellationToken: ct);
            }
        }
    }

    private static InlineKeyboardMarkup BuildTimeZoneKeyboard()
    {
        // MVP: популярные UTC-сдвиги. Позже можно добавить ввод IANA/Windows TZ.
        var rows = new[]
        {
            new[] { "UTC-01:00", "UTC+00:00", "UTC+01:00" },
            new[] { "UTC+02:00", "UTC+03:00", "UTC+04:00" },
            new[] { "UTC+05:00", "UTC+06:00", "UTC+07:00" }
        };

        return new InlineKeyboardMarkup(
            rows.Select(r => r.Select(tz => InlineKeyboardButton.WithCallbackData(tz, $"tz:{tz}")).ToArray())
                .ToArray());
    }

    public static InlineKeyboardMarkup BuildAckKeyboard(long reminderId, string cycleId)
        => new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("✅ Выпил", $"ack:{reminderId}:{cycleId}"));

    private static bool TryParseTime(string hhmm, out int minutesFromMidnight)
    {
        minutesFromMidnight = 0;
        var parts = hhmm.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var hh)) return false;
        if (!int.TryParse(parts[1], out var mm)) return false;
        if (hh < 0 || hh > 23) return false;
        if (mm < 0 || mm > 59) return false;
        minutesFromMidnight = hh * 60 + mm;
        return true;
    }

    private async Task<DateTimeOffset> CalculateNextFireAtUtc(
        long telegramUserId,
        ReminderType type,
        int? dailyMinutes,
        int? windowStartMinutes,
        int? windowEndMinutes,
        int? everyMinutes,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tzId = await db.UserProfiles
            .Where(x => x.TelegramUserId == telegramUserId)
            .Select(x => x.TimeZoneId)
            .SingleOrDefaultAsync(ct);

        var offset = ParseUtcOffsetOrZero(tzId);
        var nowLocal = nowUtc.ToOffset(offset);

        return type switch
        {
            ReminderType.DailyAtTime when dailyMinutes is int dm => CalculateNextDailyLocal(nowLocal, offset, dm).ToUniversalTime(),
            ReminderType.EveryNMinutesInWindow when windowStartMinutes is int ws && windowEndMinutes is int we && everyMinutes is int ev
                => CalculateNextInWindowLocal(nowLocal, offset, ws, we, ev).ToUniversalTime(),
            _ => nowUtc.AddDays(1)
        };
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

    private static DateTimeOffset CalculateNextInWindowLocal(DateTimeOffset nowLocal, TimeSpan offset, int windowStart, int windowEnd, int everyMinutes)
    {
        var dayStart = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, offset);
        var windowStartLocal = dayStart.AddMinutes(windowStart);
        var windowEndLocal = dayStart.AddMinutes(windowEnd);

        if (nowLocal < windowStartLocal)
            return windowStartLocal;

        if (nowLocal >= windowEndLocal)
            return windowStartLocal.AddDays(1);

        var minutesSinceStart = (nowLocal - windowStartLocal).TotalMinutes;
        var k = (int)Math.Floor(minutesSinceStart / everyMinutes);
        var candidate = windowStartLocal.AddMinutes(k * everyMinutes);
        if (candidate <= nowLocal)
            candidate = candidate.AddMinutes(everyMinutes);

        return candidate < windowEndLocal ? candidate : windowStartLocal.AddDays(1);
    }

    private static TimeSpan ParseUtcOffsetOrZero(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId))
            return TimeSpan.Zero;

        // Expected: "UTC+03:00", "UTC-01:00", "UTC+0:00"
        if (!tzId.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.Zero;

        var rest = tzId["UTC".Length..].Trim();
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
        if (hh < 0 || hh > 14) return TimeSpan.Zero;
        if (mm is not (0 or 15 or 30 or 45)) return TimeSpan.Zero;

        var ts = new TimeSpan(hh, mm, 0);
        return sign == '-' ? -ts : ts;
    }

    private async Task<string> FormatLocalAsync(DateTimeOffset utc, long telegramUserId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tzId = await db.UserProfiles
            .Where(p => p.TelegramUserId == telegramUserId)
            .Select(p => p.TimeZoneId)
            .SingleOrDefaultAsync(ct);

        var offset = ParseUtcOffsetOrZero(tzId);
        var local = utc.ToOffset(offset);
        return $"{local:yyyy-MM-dd HH:mm} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm})";
    }

    private async Task UpsertUserProfileAsync(long telegramUserId, long chatId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var profile = await db.UserProfiles.SingleOrDefaultAsync(x => x.TelegramUserId == telegramUserId, ct);
        if (profile is null)
        {
            profile = new UserProfile
            {
                TelegramUserId = telegramUserId,
                ChatId = chatId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.UserProfiles.Add(profile);
        }
        else
        {
            profile.ChatId = chatId;
            profile.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }
}


