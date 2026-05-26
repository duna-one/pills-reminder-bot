using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PillsReminderBot.Bot;

public sealed class StickerService
{
    private readonly ILogger<StickerService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly string[] _stickerSets;
    private readonly ConcurrentDictionary<string, string[]> _stickerCache = new();

    public StickerService(ILogger<StickerService> logger, ITelegramBotClient bot)
    {
        _logger = logger;
        _bot = bot;
        _stickerSets = (Environment.GetEnvironmentVariable("STICKER_SETS") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    public async Task SendRandomStickerAsync(long chatId, CancellationToken ct)
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
}
