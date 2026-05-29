using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PillsReminderBot.Domain;
using PillsReminderBot.Persistence;
using PillsReminderBot.Scheduler;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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

        if (userId is not null && Flows.TryGetValue(userId.Value, out var flow) && flow.Stage != ReminderFlowStage.None)
        {
            await HandleFlowMessageAsync(userId.Value, chatId, message.MessageId, text, flow, ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await UpsertUserProfileAsync(userId!.Value, chatId, ct);
            await ShowHomeAsync(userId.Value, chatId, null, "Привет! Я помогу не забывать про лекарства.", ct);
            await _stickerService.SendRandomStickerAsync(chatId, ct);
            return;
        }

        if (text.StartsWith("/timezone", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await UpsertUserProfileAsync(userId!.Value, chatId, ct);
            await ShowTimeZoneAsync(chatId, null, ct);
            return;
        }

        if (text.StartsWith("/new", StringComparison.OrdinalIgnoreCase) || string.Equals(text, NewButtonText, StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsureUserAsync(userId, chatId, ct))
                return;

            await StartFlowAsync(userId!.Value, chatId, null, ct);
            return;
        }

        if (text.StartsWith("/list", StringComparison.OrdinalIgnoreCase) || string.Equals(text, ListButtonText, StringComparison.OrdinalIgnoreCase))
        {
            await ShowListAsync(userId, chatId, null, null, ct);
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

        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        await ShowHomeAsync(userId!.Value, chatId, null, "Не понял сообщение. Выбери действие кнопками ниже.", ct);
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Data is null)
            return;

        var chatId = cq.Message?.Chat.Id;
        var messageId = cq.Message?.MessageId;

        if (cq.Data.StartsWith("tz:", StringComparison.Ordinal))
        {
            await HandleTimeZoneCallbackAsync(cq, ct);
            return;
        }

        if (cq.Data.Equals("home", StringComparison.Ordinal))
        {
            if (chatId is not null)
                await ShowHomeAsync(cq.From.Id, chatId.Value, messageId, null, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("timezone", StringComparison.Ordinal))
        {
            if (chatId is not null)
                await ShowTimeZoneAsync(chatId.Value, messageId, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("new", StringComparison.Ordinal))
        {
            if (chatId is not null)
                await StartFlowAsync(cq.From.Id, chatId.Value, messageId, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("cancel_flow", StringComparison.Ordinal))
        {
            Flows.TryRemove(cq.From.Id, out _);
            if (chatId is not null)
                await ShowHomeAsync(cq.From.Id, chatId.Value, messageId, "Создание напоминания отменено.", ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals("list", StringComparison.Ordinal))
        {
            if (chatId is not null)
                await ShowListAsync(cq.From.Id, chatId.Value, messageId, null, ct);

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.StartsWith(ReminderDaySelectionKeyboard.TogglePrefix, StringComparison.Ordinal)
            || cq.Data.Equals(ReminderDaySelectionKeyboard.AllCallback, StringComparison.Ordinal)
            || cq.Data.Equals(ReminderDaySelectionKeyboard.WeekdaysCallback, StringComparison.Ordinal)
            || cq.Data.Equals(ReminderDaySelectionKeyboard.OkCallback, StringComparison.Ordinal))
        {
            await HandleDaySelectionCallbackAsync(cq, ct);
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
            await ShowHomeAsync(cq.From.Id, cq.Message.Chat.Id, cq.Message.MessageId, $"Часовой пояс сохранен: {timeZoneId}", ct);
    }

    private async Task HandleDaySelectionCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (cq.Message is null)
            return;

        if (!Flows.TryGetValue(cq.From.Id, out var flow) || flow.Stage != ReminderFlowStage.AwaitingWeekDays)
        {
            await _bot.AnswerCallbackQuery(cq.Id, text: "Этот выбор уже не актуален.", cancellationToken: ct);
            return;
        }

        if (cq.Data!.StartsWith(ReminderDaySelectionKeyboard.TogglePrefix, StringComparison.Ordinal))
        {
            if (int.TryParse(cq.Data[ReminderDaySelectionKeyboard.TogglePrefix.Length..], out var bit))
                flow.WeekDaysMask = (flow.WeekDaysMask & WeekDayMask.AllDays) ^ (bit & WeekDayMask.AllDays);

            await ShowWeekDaysStepAsync(cq.Message.Chat.Id, cq.Message.MessageId, flow, ct);
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals(ReminderDaySelectionKeyboard.AllCallback, StringComparison.Ordinal))
        {
            flow.WeekDaysMask = WeekDayMask.AllDays;
            await ShowWeekDaysStepAsync(cq.Message.Chat.Id, cq.Message.MessageId, flow, ct);
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if (cq.Data.Equals(ReminderDaySelectionKeyboard.WeekdaysCallback, StringComparison.Ordinal))
        {
            flow.WeekDaysMask = WeekDayMask.Weekdays;
            await ShowWeekDaysStepAsync(cq.Message.Chat.Id, cq.Message.MessageId, flow, ct);
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
            return;
        }

        if ((flow.WeekDaysMask & WeekDayMask.AllDays) == 0)
        {
            await _bot.AnswerCallbackQuery(cq.Id, text: "Выбери хотя бы один день.", showAlert: true, cancellationToken: ct);
            return;
        }

        flow.Stage = ReminderFlowStage.AwaitingTitle;
        flow.PromptMessageId = await ShowOrEditAsync(
            cq.Message.Chat.Id,
            cq.Message.MessageId,
            BuildCreatePrompt(flow, "Теперь введи название или текст напоминания."),
            BuildCancelKeyboard(),
            ct);
        await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
    }

    private async Task HandleEditCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["edit:".Length..], out var reminderId) || cq.Message is null)
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

        await ShowReminderCardAsync(cq.From.Id, cq.Message.Chat.Id, cq.Message.MessageId, reminder, null, ct);
        await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);
    }

    private async Task HandleToggleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["toggle:".Length..], out var reminderId) || cq.Message is null)
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
        await ShowReminderCardAsync(
            cq.From.Id,
            cq.Message.Chat.Id,
            cq.Message.MessageId,
            reminder,
            reminder.IsEnabled ? "Напоминание включено." : "Напоминание выключено.",
            ct);
    }

    private async Task HandleDeleteCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        if (!long.TryParse(cq.Data!["del:".Length..], out var reminderId) || cq.Message is null)
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
        await ShowListAsync(cq.From.Id, cq.Message.Chat.Id, cq.Message.MessageId, "Напоминание удалено.", ct);
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
                await ShowOrEditAsync(
                    cq.Message.Chat.Id,
                    cq.Message.MessageId,
                    $"⏰ Отложено.\n\n{reminder.Title}\nСледующее напоминание: {nextLocalText}",
                    null,
                    ct);
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

        await _bot.AnswerCallbackQuery(cq.Id, text: "Отметил как выпито.", cancellationToken: ct);

        if (cq.Message is not null)
        {
            var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, cq.From.Id, ct);
            await ShowOrEditAsync(
                cq.Message.Chat.Id,
                cq.Message.MessageId,
                $"✅ Принято.\n\n{reminder.Title}\nСледующее напоминание: {nextLocalText}",
                null,
                ct);
        }
    }

    private async Task HandleDeleteCommandAsync(long? userId, long chatId, string text, CancellationToken ct)
    {
        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        var telegramUserId = userId!.Value;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
        {
            await ShowHomeAsync(telegramUserId, chatId, null, "Формат команды: /delete <id>", ct);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == telegramUserId, ct);
        if (reminder is null)
        {
            await ShowHomeAsync(telegramUserId, chatId, null, "Напоминание не найдено.", ct);
            return;
        }

        db.Reminders.Remove(reminder);
        await db.SaveChangesAsync(ct);
        await ShowListAsync(telegramUserId, chatId, null, "Напоминание удалено.", ct);
    }

    private async Task HandleToggleCommandAsync(long? userId, long chatId, string text, CancellationToken ct)
    {
        if (!await EnsureUserAsync(userId, chatId, ct))
            return;

        var telegramUserId = userId!.Value;
        var enable = text.StartsWith("/enable", StringComparison.OrdinalIgnoreCase);
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var id))
        {
            await ShowHomeAsync(telegramUserId, chatId, null, $"Формат команды: {(enable ? "/enable" : "/disable")} <id>", ct);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id && r.TelegramUserId == telegramUserId, ct);
        if (reminder is null)
        {
            await ShowHomeAsync(telegramUserId, chatId, null, "Напоминание не найдено.", ct);
            return;
        }

        reminder.IsEnabled = enable;
        reminder.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await ShowReminderCardAsync(telegramUserId, chatId, null, reminder, enable ? "Напоминание включено." : "Напоминание выключено.", ct);
    }

    private async Task ShowHomeAsync(long userId, long chatId, int? messageId, string? notice, CancellationToken ct)
    {
        var timeZoneId = await GetUserTimeZoneAsync(userId, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var count = await db.Reminders.CountAsync(r => r.TelegramUserId == userId, ct);
        var enabledCount = await db.Reminders.CountAsync(r => r.TelegramUserId == userId && r.IsEnabled, ct);

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(notice))
            lines.Add($"{notice}\n");

        lines.Add("Главное меню");
        lines.Add($"Часовой пояс: {(string.IsNullOrWhiteSpace(timeZoneId) ? "не выбран" : timeZoneId)}");
        lines.Add($"Напоминания: {enabledCount} включено из {count}");

        await ShowOrEditAsync(chatId, messageId, string.Join("\n", lines), BuildHomeKeyboard(), ct);
    }

    private async Task ShowTimeZoneAsync(long chatId, int? messageId, CancellationToken ct)
    {
        await ShowOrEditAsync(chatId, messageId, "Выбери свой UTC-сдвиг:", BuildTimeZoneKeyboard(), ct);
    }

    private async Task ShowListAsync(long? userId, long chatId, int? messageId, string? notice, CancellationToken ct)
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

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(notice))
            lines.Add($"{notice}\n");

        lines.Add("Список напоминаний");

        if (items.Count == 0)
        {
            lines.Add("Пока ничего нет. Создай первое напоминание.");
            await ShowOrEditAsync(chatId, messageId, string.Join("\n", lines), BuildEmptyListKeyboard(), ct);
            return;
        }

        foreach (var item in items)
        {
            var status = item.IsEnabled ? "✅" : "🚫";
            var ack = item.AwaitingAck ? " ждет подтверждения" : string.Empty;
            var nextLocal = item.NextFireAtUtc.ToOffset(offset);
            lines.Add($"{status} {item.Title}");
            lines.Add($"{FormatSchedule(item)} · следующее: {nextLocal:yyyy-MM-dd HH:mm} ({FormatOffset(offset)}){ack}");
        }

        await ShowOrEditAsync(chatId, messageId, string.Join("\n", lines), BuildReminderListKeyboard(items), ct);
    }

    private async Task ShowReminderCardAsync(long userId, long chatId, int? messageId, Reminder reminder, string? notice, CancellationToken ct)
    {
        var nextLocalText = await FormatLocalAsync(reminder.NextFireAtUtc, userId, ct);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(notice))
            lines.Add($"{notice}\n");

        lines.Add(reminder.Title);
        lines.Add(FormatSchedule(reminder));
        lines.Add($"Статус: {(reminder.IsEnabled ? "включено" : "выключено")}");
        lines.Add($"Следующее: {nextLocalText}");

        await ShowOrEditAsync(chatId, messageId, string.Join("\n", lines), BuildReminderEditKeyboard(reminder), ct);
    }

    private async Task<int?> ShowOrEditAsync(
        long chatId,
        int? messageId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken ct)
    {
        if (messageId is not null)
        {
            try
            {
                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: messageId.Value,
                    text: text,
                    replyMarkup: replyMarkup,
                    cancellationToken: ct);
                return messageId;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return messageId;
            }
            catch (ApiRequestException ex)
            {
                _logger.LogDebug(ex, "Failed to edit messageId={MessageId}; sending a new screen", messageId.Value);
            }
        }

        var sent = await _bot.SendMessage(
            chatId: chatId,
            text: text,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
        return sent.MessageId;
    }

    private static InlineKeyboardMarkup BuildHomeKeyboard()
        => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(ListButtonText, "list"),
                InlineKeyboardButton.WithCallbackData(NewButtonText, "new")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🌍 Часовой пояс", "timezone")
            }
        });

    private static InlineKeyboardMarkup BuildTimeZoneKeyboard()
    {
        var rows = new[]
        {
            new[] { "UTC-01:00", "UTC+00:00", "UTC+01:00" },
            new[] { "UTC+02:00", "UTC+03:00", "UTC+04:00" },
            new[] { "UTC+05:00", "UTC+06:00", "UTC+07:00" }
        };

        var keyboardRows = rows
            .Select(r => r.Select(tz => InlineKeyboardButton.WithCallbackData(tz, $"tz:{tz}")).ToArray())
            .ToList();
        keyboardRows.Add([InlineKeyboardButton.WithCallbackData("↩️ В меню", "home")]);

        return new InlineKeyboardMarkup(keyboardRows);
    }

    public static InlineKeyboardMarkup BuildAckKeyboard(long reminderId, string cycleId)
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ Выпил", $"ack:{reminderId}:{cycleId}") },
            new[] { InlineKeyboardButton.WithCallbackData("⏰ Отложить на 30 мин", $"snooze:{reminderId}:{cycleId}:{SnoozeMinutes}") }
        });

    private static InlineKeyboardMarkup BuildCancelKeyboard()
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "cancel_flow") }
        });

    private static InlineKeyboardMarkup BuildEmptyListKeyboard()
        => new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(NewButtonText, "new") },
            new[] { InlineKeyboardButton.WithCallbackData("↩️ В меню", "home") }
        });

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

        rows.Add([InlineKeyboardButton.WithCallbackData(NewButtonText, "new")]);
        rows.Add([InlineKeyboardButton.WithCallbackData("↩️ В меню", "home")]);

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildReminderEditKeyboard(Reminder reminder)
        => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    reminder.IsEnabled ? "Выключить" : "Включить",
                    $"toggle:{reminder.Id}"),
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
        return $"{local:yyyy-MM-dd HH:mm} ({FormatOffset(offset)})";
    }

    private static string FormatSchedule(Reminder reminder)
    {
        var days = WeekDayMask.Format(reminder.WeekDaysMask);
        return reminder.Type switch
        {
            ReminderType.DailyAtTime when reminder.DailyTimeMinutes is int dailyMinutes
                => $"{days} в {dailyMinutes / 60:D2}:{dailyMinutes % 60:D2}",
            ReminderType.EveryNMinutesInWindow
                when reminder.WindowStartMinutes is int windowStart
                     && reminder.WindowEndMinutes is int windowEnd
                     && reminder.EveryMinutes is int everyMinutes
                => $"{days}, каждые {everyMinutes} мин с {windowStart / 60:D2}:{windowStart % 60:D2} до {windowEnd / 60:D2}:{windowEnd % 60:D2}",
            _ => "расписание неизвестно"
        };
    }

    private static string FormatOffset(TimeSpan offset)
        => $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "—";

        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private async Task StartFlowAsync(long userId, long chatId, int? messageId, CancellationToken ct)
    {
        var timeZoneId = await GetUserTimeZoneAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            await ShowTimeZoneAsync(chatId, messageId, ct);
            return;
        }

        var flow = GetOrCreateFlow(userId);
        flow.Reset();
        flow.Stage = ReminderFlowStage.AwaitingDailyTime;
        flow.WeekDaysMask = WeekDayMask.AllDays;

        flow.PromptMessageId = await ShowOrEditAsync(
            chatId,
            messageId,
            BuildCreatePrompt(flow, "Укажи время в формате HH:mm, например 09:30."),
            BuildCancelKeyboard(),
            ct);
    }

    private async Task HandleFlowMessageAsync(long userId, long chatId, int userMessageId, string text, ReminderFlowState flow, CancellationToken ct)
    {
        await TryDeleteMessageAsync(chatId, userMessageId, ct);

        switch (flow.Stage)
        {
            case ReminderFlowStage.AwaitingDailyTime:
                if (!TryParseTime(text, out var dailyMinutes))
                {
                    flow.PromptMessageId = await ShowOrEditAsync(
                        chatId,
                        flow.PromptMessageId,
                        BuildCreatePrompt(flow, "Не распознал время. Введи HH:mm, например 09:30."),
                        BuildCancelKeyboard(),
                        ct);
                    return;
                }

                flow.DailyTimeMinutes = dailyMinutes;
                flow.WeekDaysMask = WeekDayMask.AllDays;
                flow.Stage = ReminderFlowStage.AwaitingWeekDays;
                await ShowWeekDaysStepAsync(chatId, flow.PromptMessageId, flow, ct);
                return;

            case ReminderFlowStage.AwaitingWeekDays:
                flow.PromptMessageId = await ShowOrEditAsync(
                    chatId,
                    flow.PromptMessageId,
                    BuildCreatePrompt(flow, "Выбери дни кнопками ниже и нажми OK."),
                    ReminderDaySelectionKeyboard.Build(flow.WeekDaysMask),
                    ct);
                return;

            case ReminderFlowStage.AwaitingTitle:
                var title = text.Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    flow.PromptMessageId = await ShowOrEditAsync(
                        chatId,
                        flow.PromptMessageId,
                        BuildCreatePrompt(flow, "Название пустое. Введи название или текст напоминания."),
                        BuildCancelKeyboard(),
                        ct);
                    return;
                }

                flow.Title = title;
                await CreateReminderFromFlowAsync(userId, chatId, flow, ct);
                Flows.TryRemove(userId, out _);
                return;
        }

        Flows.TryRemove(userId, out _);
        await ShowHomeAsync(userId, chatId, flow.PromptMessageId, "Диалог сброшен.", ct);
    }

    private async Task TryDeleteMessageAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _bot.DeleteMessage(chatId, messageId, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            _logger.LogDebug(ex, "Failed to delete user input messageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected failure deleting user input messageId={MessageId}", messageId);
        }
    }

    private async Task ShowWeekDaysStepAsync(long chatId, int? messageId, ReminderFlowState flow, CancellationToken ct)
    {
        flow.PromptMessageId = await ShowOrEditAsync(
            chatId,
            messageId,
            BuildCreatePrompt(flow, "Выбери дни, когда напоминание должно работать."),
            ReminderDaySelectionKeyboard.Build(flow.WeekDaysMask),
            ct);
    }

    private static string BuildCreatePrompt(ReminderFlowState flow, string prompt)
    {
        var lines = new List<string> { "Новое напоминание" };

        if (flow.DailyTimeMinutes is int dailyMinutes)
            lines.Add($"Время: {dailyMinutes / 60:D2}:{dailyMinutes % 60:D2}");

        if (flow.Stage is ReminderFlowStage.AwaitingWeekDays or ReminderFlowStage.AwaitingTitle)
            lines.Add($"Дни: {WeekDayMask.FormatSelection(flow.WeekDaysMask)}");

        lines.Add(string.Empty);
        lines.Add(prompt);
        return string.Join("\n", lines);
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
                WeekDaysMask = WeekDayMask.Normalize(flow.WeekDaysMask),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            reminder.NextFireAtUtc = _scheduleCalculator.CalculateNextFireAtUtc(reminder, timeZoneId, now);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.Reminders.Add(reminder);
            await db.SaveChangesAsync(ct);

            await ShowReminderCardAsync(userId, chatId, flow.PromptMessageId, reminder, "Напоминание создано.", ct);
            return;
        }

        await ShowHomeAsync(userId, chatId, flow.PromptMessageId, "Не удалось создать напоминание. Попробуй еще раз.", ct);
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
        public int? PromptMessageId { get; set; }
        public int? DailyTimeMinutes { get; set; }
        public int WeekDaysMask { get; set; } = WeekDayMask.AllDays;
        public string? Title { get; set; }

        public void Reset()
        {
            Stage = ReminderFlowStage.None;
            PromptMessageId = null;
            DailyTimeMinutes = null;
            WeekDaysMask = WeekDayMask.AllDays;
            Title = null;
        }
    }

    private enum ReminderFlowStage
    {
        None = 0,
        AwaitingDailyTime,
        AwaitingWeekDays,
        AwaitingTitle
    }
}
