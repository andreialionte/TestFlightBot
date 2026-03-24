using System.Net;
using Flurl.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace TestFlightBot;

public sealed class Bot(ILogger<Bot> logger) : BackgroundService
{
    private const string AlphaUrl = "https://testflight.apple.com/join/7lt2tesn";
    private const string BetaUrl = "https://testflight.apple.com/join/1SyedSId";

    private readonly string _token = Environment.GetEnvironmentVariable("TG_BOT_TOKEN")
                                     ?? throw new InvalidOperationException("Set TG_BOT_TOKEN environment variable.");

    private ITelegramBotClient? _telegram;
    private long _chatId;

    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("TF_POLL_MINUTES"), out var minutes) && minutes > 0
            ? minutes
            : 1);

    private readonly Dictionary<string, string> _lastStatusByName = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TestFlight monitor starting...");

        _telegram = new TelegramBotClient(_token);

        // Try to get chat ID from env, or auto-discover from first update
        if (!long.TryParse(Environment.GetEnvironmentVariable("TG_CHAT_ID"), out _chatId))
        {
            logger.LogInformation("TG_CHAT_ID not set. Waiting for first message to bot...");
            
            for (int attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    var updates = await _telegram.GetUpdatesAsync(cancellationToken: stoppingToken);
                    if (updates.Any())
                    {
                        _chatId = updates.Last().Message!.Chat.Id;
                        logger.LogInformation("✓ Auto-discovered Chat ID: {ChatId}", _chatId);
                        
                        await _telegram.SendTextMessageAsync(
                            chatId: _chatId,
                            text: "🤖 TestFlight Monitor started! Watching Spotify Alpha & Beta...",
                            cancellationToken: stoppingToken);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error auto-discovering chat ID (attempt {Attempt}/30)", attempt + 1);
                }

                await Task.Delay(1000, stoppingToken);
            }

            if (_chatId == 0)
            {
                throw new InvalidOperationException(
                    "Could not auto-discover chat ID. Set TG_CHAT_ID env var or send a message to the bot.");
            }
        }
        else
        {
            logger.LogInformation("Using Chat ID from environment: {ChatId}", _chatId);
        }

        logger.LogInformation("TestFlight monitor started. Polling every {Minutes} minute(s).", _pollInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndNotifyAsync("Spotify Alpha", AlphaUrl, stoppingToken);
            await CheckAndNotifyAsync("Spotify Beta", BetaUrl, stoppingToken);

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task CheckAndNotifyAsync(string label, string url, CancellationToken cancellationToken)
    {
        try
        {
            var html = await url
                .WithHeader("Accept-Language", "en-us")
                .GetStringAsync(cancellationToken: cancellationToken);

            var status = ParseStatus(html);
            _lastStatusByName.TryGetValue(label, out var previousStatus);
            _lastStatusByName[label] = status;

            logger.LogInformation("{Label}: {Status} (previous: {Previous})", label, status, previousStatus ?? "none");

            if (status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousStatus, "open", StringComparison.OrdinalIgnoreCase))
            {
                await _telegram!.SendTextMessageAsync(
                    chatId: _chatId,
                    text: $"🎉 {label} is OPEN! Join now: {url}",
                    cancellationToken: cancellationToken);
                
                logger.LogInformation("✓ Notification sent for {Label}", label);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed checking {Label} ({Url})", label, url);
        }
    }

    private static string ParseStatus(string html)
    {
        var decoded = WebUtility.HtmlDecode(html);

        if (decoded.Contains("This beta is full", StringComparison.OrdinalIgnoreCase))
        {
            return "full";
        }

        if (decoded.Contains("isn't accepting any new testers right now", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("is not accepting any new testers right now", StringComparison.OrdinalIgnoreCase))
        {
            return "closed";
        }

        if (decoded.Contains("Start Testing", StringComparison.OrdinalIgnoreCase))
        {
            return "open";
        }

        return "unknown";
    }
}
