using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using PillsReminderBot.Domain;
using PillsReminderBot.Persistence;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using Telegram.Bot.Types.Stickers;

namespace PillsReminderBot.Bot;

public sealed class BotUpdateHandler
{
    private readonly ILogger<BotUpdateHandler> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private static readonly ConcurrentDictionary<long, ReminderFlowState> _flows = new();
    private readonly string[] _stickerSets;
    private readonly ConcurrentDictionary<string, string[]> _stickerCache = new();

    public BotUpdateHandler(
        ILogger<BotUpdateHandler> logger,
        ITelegramBotClient bot,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _bot = bot;
        _dbFactory = dbFactory;
        var envSets = Environment.GetEnvironmentVariable("STICKER_SETS") ?? string.Empty;
        _stickerSets = envSets
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
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

        // Поддержка главного меню (reply keyboard) — эквиваленты команд
        if (string.Equals(text, "📋 Список напоминаний", StringComparison.OrdinalIgnoreCase))
        {
            await HandleListAsync(userId, chatId, ct);
            return;
        }

        if (string.Equals(text, "➕ Новое напоминание", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await StartFlowAsync(userId.Value, chatId, ct);
            return;
        }

        // Если пользователь в мастере создания — обрабатываем шаги
        if (userId is not null && _flows.TryGetValue(userId.Value, out var flow) && flow.Stage != ReminderFlowStage.None)
        {
            await HandleFlowMessageAsync(userId.Value, chatId, text, flow, ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await UpsertUserProfileAsync(userId.Value, chatId, ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var hasTimezone = await db.UserProfiles
                .Where(p => p.TelegramUserId == userId.Value)
                .Select(p => !string.IsNullOrWhiteSpace(p.TimeZoneId))
                .SingleAsync(ct);

            if (hasTimezone)
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "Привет! Я бот-напоминалка.\nМеню внизу поможет управлять напоминаниями.",
                    replyMarkup: BuildMainMenuKeyboard(),
                    cancellationToken: ct);
                await SendRandomStickerAsync(chatId, ct);
            }
            else
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "Привет! Я бот-напоминалка.\n\nСначала выбери часовой пояс командой /timezone.",
                    replyMarkup: BuildMainMenuKeyboard(),
                    cancellationToken: ct);
                await SendRandomStickerAsync(chatId, ct);
            }
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
            await SendRandomStickerAsync(chatId, ct);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            if (userId is null)
            {
                await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
                return;
            }

            await StartFlowAsync(userId.Value, chatId, ct);
            return;
        }

        if (text.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
        {
            await HandleListAsync(userId, chatId, ct);
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
                    replyMarkup: BuildMainMenuKeyboard(),
                    cancellationToken: ct);
            }

            return;
        }

        if (cq.Data.Equals("new", StringComparison.Ordinal))
        {
            if (cq.From is not null && cq.Message is not null)
            {
                await StartFlowAsync(cq.From.Id, cq.Message.Chat.Id, ct);
            }
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("cancel_flow", StringComparison.Ordinal))
        {
            if (cq.From is not null)
            {
                _flows.TryRemove(cq.From.Id, out _);
            }
            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    cq.Message.Chat.Id,
                    "Создание напоминания отменено.",
                    replyMarkup: BuildMainMenuKeyboard(),
                    cancellationToken: ct);
            }
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("list", StringComparison.Ordinal))
        {
            if (cq.Message is not null)
            {
                await HandleListAsync(cq.From.Id, cq.Message.Chat.Id, ct);
            }
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.StartsWith("edit:", StringComparison.Ordinal))
        {
            var payload = cq.Data["edit:".Length..];
            if (!long.TryParse(payload, out var reminderId))
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(
                r => r.Id == reminderId && r.TelegramUserId == cq.From.Id,
                ct);

            if (reminder is null)
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Не найдено напоминание.", cancellationToken: ct);
                return;
            }

            var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, cq.From.Id, ct);
            var schedule = FormatSchedule(reminder);
            var text = $"#{reminder.Id} {reminder.Title}\n{schedule}\nСтатус: {(reminder.IsEnabled ? "включено" : "выключено")}\nСледующее: {nextLocalText}";

            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: text,
                    replyMarkup: BuildReminderEditKeyboard(reminder),
                    cancellationToken: ct);
            }

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.StartsWith("toggle:", StringComparison.Ordinal))
        {
            var payload = cq.Data["toggle:".Length..];
            if (!long.TryParse(payload, out var reminderId))
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(
                r => r.Id == reminderId && r.TelegramUserId == cq.From.Id,
                ct);

            if (reminder is null)
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Не найдено напоминание.", cancellationToken: ct);
                return;
            }

            reminder.IsEnabled = !reminder.IsEnabled;
            reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await _bot.AnswerCallbackQuery(cq.Id, text: reminder.IsEnabled ? "Включено" : "Выключено", cancellationToken: ct);

            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: $"Напоминание #{reminder.Id} {(reminder.IsEnabled ? "включено" : "выключено")}.",
                    replyMarkup: BuildReminderEditKeyboard(reminder),
                    cancellationToken: ct);
            }

            return;
        }

        if (cq.Data.StartsWith("del:", StringComparison.Ordinal))
        {
            var payload = cq.Data["del:".Length..];
            if (!long.TryParse(payload, out var reminderId))
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = await db.Reminders.SingleOrDefaultAsync(
                r => r.Id == reminderId && r.TelegramUserId == cq.From.Id,
                ct);

            if (reminder is null)
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Не найдено напоминание.", cancellationToken: ct);
                return;
            }

            db.Reminders.Remove(reminder);
            await db.SaveChangesAsync(ct);

            await _bot.AnswerCallbackQuery(cq.Id, text: "Удалено.", cancellationToken: ct);

            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: $"Удалено напоминание #{reminder.Id}.",
                    replyMarkup: BuildMainMenuKeyboard(),
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

    private static ReplyKeyboardMarkup BuildMainMenuKeyboard()
        => new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "📋 Список напоминаний" },
            new KeyboardButton[] { "➕ Новое напоминание" }
        })
        {
            ResizeKeyboard = true
        };

    private static InlineKeyboardMarkup BuildCancelKeyboard()
        => new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("❌ Отмена", "cancel_flow"));

    private static InlineKeyboardMarkup BuildReminderListKeyboard(IEnumerable<Reminder> reminders)
    {
        var rows = reminders
            .Select(r =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"{(r.IsEnabled ? "✅" : "🚫")} #{r.Id} {Truncate(r.Title, 24)}",
                        $"edit:{r.Id}")
                })
            .ToList();

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("➕ Новое напоминание", "new") });

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildReminderEditKeyboard(Reminder reminder)
        => new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    reminder.IsEnabled ? "Выключить" : "Включить",
                    $"toggle:{reminder.Id}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"del:{reminder.Id}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("↩️ К списку", "list"),
                InlineKeyboardButton.WithCallbackData("➕ Новое", "new")
            }
        });

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

    private async Task HandleListAsync(long? userId, long chatId, CancellationToken ct)
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
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await _bot.SendMessage(
                chatId,
                "Напоминаний пока нет. Создай новое.",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
            await _bot.SendMessage(
                chatId,
                "Нажми кнопку ниже, чтобы создать:",
                replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("➕ Новое напоминание", "new")),
                cancellationToken: ct);
            await SendRandomStickerAsync(chatId, ct);
            return;
        }

        var lines = items.Select(i =>
        {
            var schedule = i.Type switch
            {
                ReminderType.DailyAtTime when i.DailyTimeMinutes is int dm
                    => $"{dm / 60:D2}:{dm % 60:D2}",
                _ => "—"
            };
            var status = i.IsEnabled ? "on" : "off";
            var ack = i.AwaitingAck ? " (ждёт ✅)" : string.Empty;
            var nextLocal = i.NextFireAtUtc.ToOffset(offset);
            return $"#{i.Id} [{status}]{ack} {schedule} — {i.Title} | next: {nextLocal:yyyy-MM-dd HH:mm} ({offset:hh\\:mm})";
        });

        await _bot.SendMessage(
            chatId,
            string.Join("\n", lines),
            replyMarkup: BuildReminderListKeyboard(items),
            cancellationToken: ct);
    }

    private static string FormatSchedule(Reminder r)
    {
        return r.Type switch
        {
            ReminderType.DailyAtTime when r.DailyTimeMinutes is int dm
                => $"Каждый день в {dm / 60:D2}:{dm % 60:D2}",
            _ => "Расписание неизвестно"
        };
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "—";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private async Task<string?> PickRandomStickerAsync(CancellationToken ct)
    {
        if (_stickerSets.Length == 0)
            return null;

        var setName = _stickerSets[Random.Shared.Next(_stickerSets.Length)];
        if (!_stickerCache.TryGetValue(setName, out var stickers))
        {
            try
            {
                var set = await _bot.GetStickerSet(setName, cancellationToken: ct);
                stickers = set.Stickers
                    .Where(s => s.Type == StickerType.Regular)
                    .Select(s => s.FileId)
                    .ToArray();
                _stickerCache[setName] = stickers;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load sticker set {Set}", setName);
                return null;
            }
        }

        if (stickers.Length == 0)
            return null;

        return stickers[Random.Shared.Next(stickers.Length)];
    }

    private async Task SendRandomStickerAsync(long chatId, CancellationToken ct)
    {
        var fileId = await PickRandomStickerAsync(ct);
        if (fileId is null)
            return;

        try
        {
            await _bot.SendSticker(chatId, InputFile.FromFileId(fileId), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send sticker to chatId={ChatId}", chatId);
        }
    }

    private async Task StartFlowAsync(long userId, long chatId, CancellationToken ct)
    {
        // Требуем заданную таймзону для корректных расчётов
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tz = await db.UserProfiles
            .Where(p => p.TelegramUserId == userId)
            .Select(p => p.TimeZoneId)
            .SingleOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(tz))
        {
            await _bot.SendMessage(
                chatId,
                "Сначала задай часовой пояс командой /timezone.",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
            return;
        }

        var flow = GetOrCreateFlow(userId);
        flow.Reset();
        flow.Stage = ReminderFlowStage.AwaitingDailyTime;

        await _bot.SendMessage(
            chatId,
            "Укажи время в формате HH:mm (например, 09:30).",
            replyMarkup: BuildCancelKeyboard(),
            cancellationToken: ct);
    }

    private async Task HandleFlowMessageAsync(long userId, long chatId, string text, ReminderFlowState flow, CancellationToken ct)
    {
        switch (flow.Stage)
        {
            case ReminderFlowStage.AwaitingDailyTime:
                if (!TryParseTime(text, out var dailyMinutes))
                {
                    await _bot.SendMessage(chatId, "Неверный формат. Введи время HH:mm, например 09:30.", replyMarkup: BuildCancelKeyboard(), cancellationToken: ct);
                    return;
                }
                flow.DailyTimeMinutes = dailyMinutes;
                flow.Stage = ReminderFlowStage.AwaitingTitle;
                await _bot.SendMessage(chatId, "Введи название/текст напоминания.", replyMarkup: BuildCancelKeyboard(), cancellationToken: ct);
                return;

            case ReminderFlowStage.AwaitingIntervalStart:
                if (!TryParseTime(text, out var startMinutes))
                {
                    await _bot.SendMessage(chatId, "Неверный формат. Введи время HH:mm, например 09:00.", replyMarkup: BuildCancelKeyboard(), cancellationToken: ct);
                    return;
                }
                flow.DailyTimeMinutes = startMinutes;
                flow.Stage = ReminderFlowStage.AwaitingTitle;
                await _bot.SendMessage(chatId, "Введи название/текст напоминания.", replyMarkup: BuildCancelKeyboard(), cancellationToken: ct);
                return;

            case ReminderFlowStage.AwaitingTitle:
                var title = text.Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    await _bot.SendMessage(chatId, "Текст пустой. Введи название напоминания.", replyMarkup: BuildCancelKeyboard(), cancellationToken: ct);
                    return;
                }
                flow.Title = title;
                await CreateReminderFromFlowAsync(userId, chatId, flow, ct);
                _flows.TryRemove(userId, out _);
                return;
        }

        // Любой другой случай — сброс мастера
        _flows.TryRemove(userId, out _);
        await _bot.SendMessage(chatId, "Диалог сброшен. Нажми «➕ Новое напоминание», чтобы начать заново.", replyMarkup: BuildMainMenuKeyboard(), cancellationToken: ct);
    }

    private ReminderFlowState GetOrCreateFlow(long userId)
        => _flows.GetOrAdd(userId, _ => new ReminderFlowState());

    private async Task CreateReminderFromFlowAsync(long userId, long chatId, ReminderFlowState flow, CancellationToken ct)
    {
        await UpsertUserProfileAsync(userId, chatId, ct);

        var now = DateTimeOffset.UtcNow;

        if (flow.DailyTimeMinutes is int dm)
        {
            var nextFireAtUtc = await CalculateNextFireAtUtc(
                telegramUserId: userId,
                type: ReminderType.DailyAtTime,
                dailyMinutes: dm,
                windowStartMinutes: null,
                windowEndMinutes: null,
                everyMinutes: null,
                nowUtc: now,
                ct: ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var reminder = new Reminder
            {
                TelegramUserId = userId,
                Title = flow.Title ?? string.Empty,
                Message = flow.Title ?? string.Empty,
                Type = ReminderType.DailyAtTime,
                DailyTimeMinutes = dm,
                NextFireAtUtc = nextFireAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            var nextLocalText = await FormatLocalAsync(nextFireAtUtc, userId, ct);
            await _bot.SendMessage(
                chatId,
                $"Создал напоминание #{reminder.Id}: каждый день в {dm / 60:D2}:{dm % 60:D2}.\nСледующий раз: {nextLocalText}",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
            await SendRandomStickerAsync(chatId, ct);
            return;
        }

        await _bot.SendMessage(chatId, "Не удалось создать напоминание. Попробуй ещё раз.", replyMarkup: BuildMainMenuKeyboard(), cancellationToken: ct);
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

    private sealed class ReminderFlowState
    {
        public ReminderFlowStage Stage { get; set; } = ReminderFlowStage.None;
        public int? DailyTimeMinutes { get; set; }
        public string? Title { get; set; }

        public void Reset()
        {
            Stage = ReminderFlowStage.None;
            DailyTimeMinutes = null;
            Title = null;
        }
    }

    private enum ReminderFlowStage
    {
        None = 0,
        AwaitingDailyTime,
        AwaitingTitle
    }
}


