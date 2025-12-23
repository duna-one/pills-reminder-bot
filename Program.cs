using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PillsReminderBot.Bot;
using PillsReminderBot.Persistence;
using PillsReminderBot.Scheduler;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;

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
var me = await host.Services.GetRequiredService<ITelegramBotClient>().GetMe();
logger.LogInformation("Bot started as @{Username} (id={Id})", me.Username, me.Id);

await host.RunAsync();
