using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PillsReminderBot.Bot;
using PillsReminderBot.Persistence;
using PillsReminderBot.Scheduler;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var botToken = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (string.IsNullOrWhiteSpace(botToken))
    throw new InvalidOperationException("Environment variable BOT_TOKEN is required.");

var sqliteCs =
    builder.Configuration["ConnectionStrings:Sqlite"]
    ?? Environment.GetEnvironmentVariable("SQLITE_CONNECTION_STRING")
    ?? "Data Source=pills.db";

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(sqliteCs));

builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(botToken));
builder.Services.AddSingleton<BotUpdateHandler>();
builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<ReminderSchedulerService>();

using var host = builder.Build();

// Ensure DB exists (MVP: without migrations)
await using (var db = host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
{
    await db.Database.EnsureCreatedAsync();
}

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var botClient = host.Services.GetRequiredService<ITelegramBotClient>();
var me = await botClient.GetMe();
logger.LogInformation("Bot started as @{Username} (id={Id})", me.Username, me.Id);

// Настроим команды бота, чтобы меню Telegram показывало основные действия
await botClient.SetMyCommands(
[
    new BotCommand { Command = "start", Description = "Приветствие" },
    new BotCommand { Command = "timezone", Description = "Выбор часового пояса" },
    new BotCommand { Command = "new", Description = "Новое напоминание" },
    new BotCommand { Command = "newi", Description = "Новое в интервале" },
    new BotCommand { Command = "list", Description = "Список напоминаний" },
    new BotCommand { Command = "delete", Description = "Удалить напоминание" },
    new BotCommand { Command = "enable", Description = "Включить напоминание" },
    new BotCommand { Command = "disable", Description = "Выключить напоминание" }
],
    new BotCommandScopeDefault(),
    cancellationToken: CancellationToken.None);

await host.RunAsync();
