using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PillsReminderBot.Domain;
using PillsReminderBot.Persistence;
using PillsReminderBot.Scheduler;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PillsReminderBot.Bot;

public sealed class BotUpdateHandler
{
    private const string ListButtonText = "📋 Список напоминаний";
    private const string NewButtonText = "➕ Новое напоминание";
    private const int SnoozeMinutes = 30;

    private static readonly ConcurrentDictionary<long, ReminderFlowState> Flows = new();

    private readonly ILogger<BotUpdateHandler> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ReminderScheduleCalculator _scheduleCalculator;
    private readonly StickerService _stickerService;

    public BotUpdateHandler(
        ILogger<BotUpdateHandler> logger,
        ITelegramBotClient bot,
        IDbContextFactory<AppDbContext> dbFactory,
        ReminderScheduleCalculator scheduleCalculator,
        StickerService stickerService)
    {
        _logger = logger;
        _bot = bot;
        _dbFactory = dbFactory;
        _scheduleCalculator = scheduleCalculator;
        _stickerService = stickerService;
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

        _logger.LogInformation("Text message from chatId={ChatId}, userId={UserId}, length={Length}", chatId, userId, text.Length);

        if (string.Equals(text, ListButtonText, StringComparison.OrdinalIgnoreCase))
        {
            await HandleListAsync(userId, chatId, ct);
            return;
        }

        if (string.Equals(text, NewButtonText, StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await StartFlowAsync(userId!.Value, chatId, ct);
            return;
        }

        if (userId is not null && Flows.TryGetValue(userId.Value, out var flow) && flow.Stage != ReminderFlowStage.None)
        {
            await HandleFlowMessageAsync(userId.Value, chatId, text, flow, ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await UpsertUserProfileAsync(userId!.Value, chatId, ct);
            var hasTimezone = !string.IsNullOrWhiteSpace(await GetUserTimeZoneAsync(userId.Value, ct));

            var startText = hasTimezone
                ? "Привет! Я бот-напоминалка.\nМеню внизу поможет управлять напоминаниями."
                : "Привет! Я бот-напоминалка.\n\nСначала выбери часовой пояс командой /timezone.";

            await _bot.SendMessage(
                chatId: chatId,
                text: startText,
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
            await _stickerService.SendRandomStickerAsync(chatId, ct);
            return;
        }

        if (text.StartsWith("/timezone", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await UpsertUserProfileAsync(userId!.Value, chatId, ct);
            await _bot.SendMessage(
                chatId: chatId,
                text: "Выбери свой UTC-сдвиг:",
                replyMarkup: BuildTimeZoneKeyboard(),
                cancellationToken: ct);
            await _stickerService.SendRandomStickerAsync(chatId, ct);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await StartFlowAsync(userId!.Value, chatId, ct);
            return;
        }

        if (text.StartsWith("/list", StringComparison.OrdinalIgnoreCase))
        {
            await HandleListAsync(userId, chatId, ct);
            return;
        }

        if (text.StartsWith("/delete", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDeleteCommandAsync(userId, chatId, text, ct);
            return;
        }

        if (text.StartsWith("/disable", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/enable", StringComparison.OrdinalIgnoreCase))
        {
            await HandleToggleCommandAsync(userId, chatId, text, ct);
            return;
        }

        await _bot.SendMessage(chatId, $"Вы написали: {text}", cancellationToken: ct);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Data is null)
            return;

        if (cq.Data.StartsWith("tz:", StringComparison.Ordinal))
        {
            await HandleTimeZoneCallbackAsync(cq, ct);
            return;
        }

        if (cq.Data.Equals("new", StringComparison.Ordinal))
        {
            if (cq.Message is not null)
                await StartFlowAsync(cq.From.Id, cq.Message.Chat.Id, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("cancel_flow", StringComparison.Ordinal))
        {
            Flows.TryRemove(cq.From.Id, out _);
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
                await HandleListAsync(cq.From.Id, cq.Message.Chat.Id, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.StartsWith("edit:", StringComparison.Ordinal))
        {
            await HandleEditCallbackAsync(cq, ct);
            return;
        }

        if (cq.Data.StartsWith("toggle:", StringComparison.Ordinal))
        {
            await HandleToggleCallbackAsync(cq, ct);
            return;
        }

        if (cq.Data.StartsWith("del:", StringComparison.Ordinal))
        {
            await HandleDeleteCallbackAsync(cq, ct);
            return;
        }

        if (ReminderCallbackData.TryParse(cq.Data, out var reminderCallback))
            await HandleReminderCallbackAsync(cq, reminderCallback!, ct);
    }

    private async Task HandleTimeZoneCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        var timeZoneId = cq.Data!["tz:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var profile = await db.UserProfiles.SingleOrDefaultAsync(x => x.TelegramUserId == cq.From.Id, ct);
        if (profile is null)
        {
            profile = new UserProfile
            {
                TelegramUserId = cq.From.Id,
                ChatId = cq.Message?.Chat.Id ?? 0,
                CreatedAtUtc = now
            };
            db.UserProfiles.Add(profile);
        }

        if (cq.Message is not null)
            profile.ChatId = cq.Message.Chat.Id;

        profile.TimeZoneId = timeZoneId;
        profile.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        await _bot.AnswerCallbackQuery(cq.Id, text: $"Часовой пояс сохранен: {timeZoneId}", cancellationToken: ct);

        if (cq.Message is not null)
        {
            await _bot.SendMessage(
                chatId: cq.Message.Chat.Id,
                text: $"Ок! Сохранил часовой пояс: {timeZoneId}",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
        }
    }

    private async Task HandleEditCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["edit:".Length..], out var reminderId))
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

        var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, cq.From.Id, ct);
        var text =
            $"{reminder.Title}\n{FormatSchedule(reminder)}\nСтатус: {(reminder.IsEnabled ? "включено" : "выключено")}\nСледующее: {nextLocalText}";

        if (cq.Message is not null)
        {
            await _bot.SendMessage(
                chatId: cq.Message.Chat.Id,
                text: text,
                replyMarkup: BuildReminderEditKeyboard(reminder),
                cancellationToken: ct);
        }

        await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
    }

    private async Task HandleToggleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["toggle:".Length..], out var reminderId))
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

        reminder.IsEnabled = !reminder.IsEnabled;
        reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await _bot.AnswerCallbackQuery(cq.Id, text: reminder.IsEnabled ? "Включено" : "Выключено", cancellationToken: ct);

        if (cq.Message is not null)
        {
            await _bot.SendMessage(
                chatId: cq.Message.Chat.Id,
                text: $"Напоминание {(reminder.IsEnabled ? "включено" : "выключено")}.",
                replyMarkup: BuildReminderEditKeyboard(reminder),
                cancellationToken: ct);
        }
    }

    private async Task HandleDeleteCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["del:".Length..], out var reminderId))
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

        db.Reminders.Remove(reminder);
        await db.SaveChangesAsync(ct);

        await _bot.AnswerCallbackQuery(cq.Id, text: "Удалено.", cancellationToken: ct);

        if (cq.Message is not null)
        {
            await _bot.SendMessage(
                chatId: cq.Message.Chat.Id,
                text: $"Напоминание удалено!",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
        }
    }

    private async Task HandleReminderCallbackAsync(CallbackQuery cq, ReminderCallbackData callback, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(
            r => r.Id == callback.ReminderId && r.TelegramUserId == cq.From.Id,
            ct);

        if (reminder is null)
        {
            await _bot.AnswerCallbackQuery(cq.Id, text: "Напоминание не найдено.", cancellationToken: ct);
            return;
        }

        if (!string.Equals(reminder.ActiveCycleId, callback.CycleId, StringComparison.Ordinal))
        {
            await _bot.AnswerCallbackQuery(cq.Id, text: "Эта кнопка уже не актуальна.", cancellationToken: ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (callback.Action == ReminderCallbackAction.Snooze)
        {
            if (!ReminderCallbackApplier.TrySnooze(reminder, callback.CycleId, now, callback.SnoozeMinutes!.Value))
            {
                await _bot.AnswerCallbackQuery(cq.Id, text: "Эта кнопка уже не актуальна.", cancellationToken: ct);
                return;
            }

            await db.SaveChangesAsync(ct);

            var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, cq.From.Id, ct);
            await _bot.AnswerCallbackQuery(cq.Id, text: $"Отложено на {callback.SnoozeMinutes} мин.", cancellationToken: ct);

            if (cq.Message is not null)
            {
                await _bot.SendMessage(
                    chatId: cq.Message.Chat.Id,
                    text: $"⏰ Отложил. Следующее напоминание: {nextLocalText}",
                    cancellationToken: ct);
            }

            return;
        }

        reminder.AwaitingAck = false;
        reminder.ActiveCycleId = null;
        reminder.LastAcknowledgedAtUtc = now;
        reminder.UpdatedAtUtc = now;

        var timeZoneId = await db.UserProfiles
            .Where(p => p.TelegramUserId == cq.From.Id)
            .Select(p => p.TimeZoneId)
            .SingleOrDefaultAsync(ct);

        reminder.NextFireAtUtc = _scheduleCalculator.CalculateNextFireAtUtc(reminder, timeZoneId, now);
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

    private async Task HandleDeleteCommandAsync(long? userId, long chatId, string text, CancellationToken ct)
    {
        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
        {
            await _bot.SendMessage(chatId, "Формат: /delete <id>", cancellationToken: ct);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId!.Value, ct);
        if (reminder is null)
        {
            await _bot.SendMessage(chatId, "Напоминание не найдено.", cancellationToken: ct);
            return;
        }

        db.Reminders.Remove(reminder);
        await db.SaveChangesAsync(ct);
        await _bot.SendMessage(chatId, "Удалено напоминание.", cancellationToken: ct);
    }

    private async Task HandleToggleCommandAsync(long? userId, long chatId, string text, CancellationToken ct)
    {
        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        var enable = text.StartsWith("/enable", StringComparison.OrdinalIgnoreCase);
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
        {
            await _bot.SendMessage(chatId, $"Формат: {(enable ? "/enable" : "/disable")} <id>", cancellationToken: ct);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == userId!.Value, ct);
        if (reminder is null)
        {
            await _bot.SendMessage(chatId, "Напоминание не найдено.", cancellationToken: ct);
            return;
        }

        reminder.IsEnabled = enable;
        reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await _bot.SendMessage(chatId, $"Ок. Напоминание {(enable ? "включено" : "выключено")}.", cancellationToken: ct);
    }

    private static InlineKeyboardMarkup BuildTimeZoneKeyboard()
    {
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
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ Выпил", $"ack:{reminderId}:{cycleId}") },
            new[] { InlineKeyboardButton.WithCallbackData("⏰ Отложить на 30 мин", $"snooze:{reminderId}:{cycleId}:{SnoozeMinutes}") }
        });

    private static ReplyKeyboardMarkup BuildMainMenuKeyboard()
        => new(new[]
        {
            new KeyboardButton[] { ListButtonText },
            new KeyboardButton[] { NewButtonText }
        })
        {
            ResizeKeyboard = true
        };

    private static InlineKeyboardMarkup BuildCancelKeyboard()
        => new(InlineKeyboardButton.WithCallbackData("❌ Отмена", "cancel_flow"));

    private static InlineKeyboardMarkup BuildReminderListKeyboard(IEnumerable<Reminder> reminders)
    {
        var rows = reminders
            .Select(r =>
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"{(r.IsEnabled ? "✅" : "🚫")} {Truncate(r.Title, 28)}",
                        $"edit:{r.Id}")
                })
            .ToList();

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData(NewButtonText, "new") });

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildReminderEditKeyboard(Reminder reminder)
        => new(new[]
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
        if (hh is < 0 or > 23) return false;
        if (mm is < 0 or > 59) return false;
        minutesFromMidnight = hh * 60 + mm;
        return true;
    }

    private async Task<string> FormatLocalAsync(DateTimeOffset utc, long telegramUserId, CancellationToken ct)
    {
        var timeZoneId = await GetUserTimeZoneAsync(telegramUserId, ct);
        var offset = ReminderScheduleCalculator.ParseUtcOffsetOrZero(timeZoneId);
        var local = utc.ToOffset(offset);
        return $"{local:yyyy-MM-dd HH:mm} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm})";
    }

    private async Task HandleListAsync(long? userId, long chatId, CancellationToken ct)
    {
        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var timeZoneId = await GetUserTimeZoneAsync(userId!.Value, ct);
        var offset = ReminderScheduleCalculator.ParseUtcOffsetOrZero(timeZoneId);

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
                replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(NewButtonText, "new")),
                cancellationToken: ct);
            await _stickerService.SendRandomStickerAsync(chatId, ct);
            return;
        }

        var lines = items.Select(i =>
        {
            var status = i.IsEnabled ? "on" : "off";
            var ack = i.AwaitingAck ? " (ждет ✅)" : string.Empty;
            var nextLocal = i.NextFireAtUtc.ToOffset(offset);
            return $"[{status}]{ack} {FormatSchedule(i)} — {i.Title} | next: {nextLocal:yyyy-MM-dd HH:mm} ({FormatOffset(offset)})";
        });

        await _bot.SendMessage(
            chatId,
            string.Join("\n", lines),
            replyMarkup: BuildReminderListKeyboard(items),
            cancellationToken: ct);
    }

    private static string FormatSchedule(Reminder reminder)
        => reminder.Type switch
        {
            ReminderType.DailyAtTime when reminder.DailyTimeMinutes is int dailyMinutes
                => $"каждый день в {dailyMinutes / 60:D2}:{dailyMinutes % 60:D2}",
            ReminderType.EveryNMinutesInWindow
                when reminder.WindowStartMinutes is int windowStart
                     && reminder.WindowEndMinutes is int windowEnd
                     && reminder.EveryMinutes is int everyMinutes
                => $"каждые {everyMinutes} мин с {windowStart / 60:D2}:{windowStart % 60:D2} до {windowEnd / 60:D2}:{windowEnd % 60:D2}",
            _ => "расписание неизвестно"
        };

    private static string FormatOffset(TimeSpan offset)
        => $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "—";

        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private async Task StartFlowAsync(long userId, long chatId, CancellationToken ct)
    {
        var timeZoneId = await GetUserTimeZoneAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(timeZoneId))
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
                    await _bot.SendMessage(
                        chatId,
                        "Неверный формат. Введи время HH:mm, например 09:30.",
                        replyMarkup: BuildCancelKeyboard(),
                        cancellationToken: ct);
                    return;
                }

                flow.DailyTimeMinutes = dailyMinutes;
                flow.Stage = ReminderFlowStage.AwaitingTitle;
                await _bot.SendMessage(
                    chatId,
                    "Введи название/текст напоминания.",
                    replyMarkup: BuildCancelKeyboard(),
                    cancellationToken: ct);
                return;

            case ReminderFlowStage.AwaitingTitle:
                var title = text.Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    await _bot.SendMessage(
                        chatId,
                        "Текст пустой. Введи название напоминания.",
                        replyMarkup: BuildCancelKeyboard(),
                        cancellationToken: ct);
                    return;
                }

                flow.Title = title;
                await CreateReminderFromFlowAsync(userId, chatId, flow, ct);
                Flows.TryRemove(userId, out _);
                return;
        }

        Flows.TryRemove(userId, out _);
        await _bot.SendMessage(
            chatId,
            $"Диалог сброшен. Нажми «{NewButtonText}», чтобы начать заново.",
            replyMarkup: BuildMainMenuKeyboard(),
            cancellationToken: ct);
    }

    private ReminderFlowState GetOrCreateFlow(long userId)
        => Flows.GetOrAdd(userId, _ => new ReminderFlowState());

    private async Task CreateReminderFromFlowAsync(long userId, long chatId, ReminderFlowState flow, CancellationToken ct)
    {
        await UpsertUserProfileAsync(userId, chatId, ct);

        var now = DateTimeOffset.UtcNow;
        var timeZoneId = await GetUserTimeZoneAsync(userId, ct);

        if (flow.DailyTimeMinutes is int dailyMinutes)
        {
            var reminder = new Reminder
            {
                TelegramUserId = userId,
                Title = flow.Title ?? string.Empty,
                Message = flow.Title ?? string.Empty,
                Type = ReminderType.DailyAtTime,
                DailyTimeMinutes = dailyMinutes,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            reminder.NextFireAtUtc = _scheduleCalculator.CalculateNextFireAtUtc(reminder, timeZoneId, now);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, userId, ct);
            await _bot.SendMessage(
                chatId,
                $"Создал напоминание #{reminder.Id}: каждый день в {dailyMinutes / 60:D2}:{dailyMinutes % 60:D2}.\nСледующий раз: {nextLocalText}",
                replyMarkup: BuildMainMenuKeyboard(),
                cancellationToken: ct);
            await _stickerService.SendRandomStickerAsync(chatId, ct);
            return;
        }

        await _bot.SendMessage(
            chatId,
            "Не удалось создать напоминание. Попробуй еще раз.",
            replyMarkup: BuildMainMenuKeyboard(),
            cancellationToken: ct);
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

    private async Task<string?> GetUserTimeZoneAsync(long telegramUserId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.UserProfiles
            .Where(p => p.TelegramUserId == telegramUserId)
            .Select(p => p.TimeZoneId)
            .SingleOrDefaultAsync(ct);
    }

    private async Task<bool> EnsureUserAsync(long? userId, long chatId, CancellationToken ct)
    {
        if (userId is not null)
            return true;

        await _bot.SendMessage(chatId, "Не удалось определить пользователя Telegram.", cancellationToken: ct);
        return false;
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
