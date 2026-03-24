using System.Net;
using Flurl.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

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
    private int _lastUpdateId = 0;

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
                        _lastUpdateId = updates.Last().Id;
                        logger.LogInformation("✓ Auto-discovered Chat ID: {ChatId}", _chatId);
                        
                        await _telegram.SendTextMessageAsync(
                            chatId: _chatId,
                            text: "🤖 TestFlight Monitor started! Watching Spotify Alpha & Beta...\n\nCommands:\n/check - Check status now",
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

        // Run two concurrent tasks: TestFlight monitoring + Telegram command handling
        await Task.WhenAll(
            MonitorTestFlightAsync(stoppingToken),
            HandleTelegramCommandsAsync(stoppingToken)
        );
    }

    private async Task MonitorTestFlightAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckAndNotifyAsync("Spotify Alpha", AlphaUrl, cancellationToken);
            await CheckAndNotifyAsync("Spotify Beta", BetaUrl, cancellationToken);

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private async Task HandleTelegramCommandsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _telegram!.GetUpdatesAsync(
                    offset: _lastUpdateId + 1,
                    cancellationToken: cancellationToken);

                foreach (var update in updates)
                {
                    _lastUpdateId = update.Id;

                    if (update.Message?.Text?.StartsWith("/check", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await HandleCheckCommandAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error handling Telegram commands");
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task HandleCheckCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            var alphaStatus = await GetStatusAsync(AlphaUrl, cancellationToken);
            var betaStatus = await GetStatusAsync(BetaUrl, cancellationToken);

            var GetEmoji = (string status) => status switch
            {
                "open" => "🟢",
                "full" => "🔴",
                "closed" => "🔒",
                _ => "❓"
            };

            var message = $"📊 TestFlight Status Check:\n\n" +
                         $"{GetEmoji(alphaStatus)} Spotify Alpha: {alphaStatus.ToUpper()}\n" +
                         $"{GetEmoji(betaStatus)} Spotify Beta: {betaStatus.ToUpper()}";

            await _telegram!.SendTextMessageAsync(
                chatId: _chatId,
                text: message,
                cancellationToken: cancellationToken);

            logger.LogInformation("✓ Sent status check to user");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error handling /check command");
            await _telegram!.SendTextMessageAsync(
                chatId: _chatId,
                text: "❌ Error checking status. Please try again.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task CheckAndNotifyAsync(string label, string url, CancellationToken cancellationToken)
    {
        try
        {
            var status = await GetStatusAsync(url, cancellationToken);
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

    private async Task<string> GetStatusAsync(string url, CancellationToken cancellationToken)
    {
        var html = await url
            .WithHeader("Accept-Language", "en-us")
            .GetStringAsync(cancellationToken: cancellationToken);

        return ParseStatus(html);
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
