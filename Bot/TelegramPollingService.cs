using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace PillsReminderBot.Bot;

public sealed class TelegramPollingService : BackgroundService
{
    private readonly ILogger<TelegramPollingService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly BotUpdateHandler _handler;

    public TelegramPollingService(
        ILogger<TelegramPollingService> logger,
        ITelegramBotClient bot,
        BotUpdateHandler handler)
    {
        _logger = logger;
        _bot = bot;
        _handler = handler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [],
            DropPendingUpdates = true
        };

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Telegram long polling started");

        // StartReceiving работает в фоне; держим сервис живым до остановки.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        _logger.LogInformation("Telegram long polling stopped");
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
        => _handler.HandleUpdateAsync(update, ct);

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogError(exception, "Polling error: {ErrorMessage}", errorMessage);
        return Task.CompletedTask;
    }
}


