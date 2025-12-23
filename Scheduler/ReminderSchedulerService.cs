using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PillsReminderBot.Bot;
using PillsReminderBot.Domain;
using PillsReminderBot.Persistence;
using Telegram.Bot;

namespace PillsReminderBot.Scheduler;

public sealed class ReminderSchedulerService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromHours(2);

    private readonly ILogger<ReminderSchedulerService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ReminderSchedulerService(
        ILogger<ReminderSchedulerService> logger,
        ITelegramBotClient bot,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger = logger;
        _bot = bot;
        _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Environment.GetEnvironmentVariable("SCHEDULER_POLL_SECONDS");
        var pollInterval = int.TryParse(pollSeconds, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : DefaultPollInterval;

        var repeatMinutes = Environment.GetEnvironmentVariable("REPEAT_UNTIL_ACK_MINUTES");
        var repeatInterval = int.TryParse(repeatMinutes, out var rm) && rm > 0
            ? TimeSpan.FromMinutes(rm)
            : TimeSpan.FromHours(2);

        _logger.LogInformation("Reminder scheduler started. Poll interval: {PollInterval}", pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(repeatInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed");
            }

            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Reminder scheduler stopped");
    }

    private async Task TickAsync(TimeSpan repeatInterval, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Grab a batch of due reminders.
        var due = await db.Reminders
            .Where(r => r.IsEnabled && r.NextFireAtUtc <= now)
            .OrderBy(r => r.NextFireAtUtc)
            .Take(100)
            .ToListAsync(ct);

        if (due.Count == 0)
            return;

        _logger.LogInformation("Scheduler tick: {Count} due reminders", due.Count);

        // We need user chat ids to deliver messages.
        var userIds = due.Select(r => r.TelegramUserId).Distinct().ToArray();
        var chats = await db.UserProfiles
            .Where(p => userIds.Contains(p.TelegramUserId))
            .Select(p => new { p.TelegramUserId, p.ChatId })
            .ToDictionaryAsync(x => x.TelegramUserId, x => x.ChatId, ct);

        foreach (var r in due)
        {
            if (!chats.TryGetValue(r.TelegramUserId, out var chatId) || chatId == 0)
            {
                _logger.LogWarning("No chatId for TelegramUserId={UserId}, skipping reminderId={ReminderId}", r.TelegramUserId, r.Id);
                // Push it forward to avoid tight loop.
                r.NextFireAtUtc = now.Add(repeatInterval);
                r.UpdatedAtUtc = now;
                continue;
            }

            // If this is the first fire of a new cycle, create a cycle id.
            if (!r.AwaitingAck || string.IsNullOrWhiteSpace(r.ActiveCycleId))
            {
                r.AwaitingAck = true;
                r.ActiveCycleId = Guid.NewGuid().ToString("N");
            }

            try
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: string.IsNullOrWhiteSpace(r.Message) ? r.Title : r.Message,
                    replyMarkup: BotUpdateHandler.BuildAckKeyboard(r.Id, r.ActiveCycleId),
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send reminderId={ReminderId} to chatId={ChatId}", r.Id, chatId);
                // Don't advance aggressively on send failure; try again next tick.
                continue;
            }

            r.LastFiredAtUtc = now;
            r.NextFireAtUtc = now.Add(repeatInterval);
            r.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }
}


