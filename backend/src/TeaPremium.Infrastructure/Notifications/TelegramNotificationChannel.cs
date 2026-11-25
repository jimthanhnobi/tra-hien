using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeaPremium.Application.Notifications;
using TeaPremium.Application.Time;

namespace TeaPremium.Infrastructure.Notifications;

// Per-chat Telegram rate limiter: >= 1.1s between messages and <= 18 messages/minute/chat.
// Uses IClock + TimeProvider (no direct wall-clock calls, fully deterministic in tests).
public sealed class TelegramRateLimiter(IClock clock, TimeProvider timeProvider, TimeSpan? minSpacing = null)
{
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMilliseconds(1100);
    public static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);
    public const int MaxMessagesPerWindow = 18;

    private readonly TimeSpan _minSpacing = minSpacing ?? MinSpacing;
    private readonly ConcurrentDictionary<string, ChatLimiter> _buckets = new(StringComparer.Ordinal);

    public async Task WaitTurnAsync(string chatKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatKey);
        var bucket = _buckets.GetOrAdd(chatKey, static _ => new ChatLimiter());
        await bucket.Gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = clock.UtcNow;
                while (bucket.SentTimestamps.Count > 0 && now - bucket.SentTimestamps.Peek() >= WindowDuration)
                    bucket.SentTimestamps.Dequeue();

                var spacingWait = bucket.LastSentAt is { } last
                    ? (last + _minSpacing) - now
                    : TimeSpan.Zero;
                var windowWait = bucket.SentTimestamps.Count >= MaxMessagesPerWindow
                    ? (bucket.SentTimestamps.Peek() + WindowDuration) - now
                    : TimeSpan.Zero;

                var delay = spacingWait > windowWait ? spacingWait : windowWait;
                if (delay <= TimeSpan.Zero)
                {
                    bucket.LastSentAt = now;
                    bucket.SentTimestamps.Enqueue(now);
                    return;
                }

                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    private sealed class ChatLimiter
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset? LastSentAt { get; set; }
        public Queue<DateTimeOffset> SentTimestamps { get; } = new();
    }
}

public sealed class TelegramHttpClient : IDisposable
{
    private readonly HttpClient _client;

    public TelegramHttpClient(HttpMessageHandler handler, NotificationOptions options, bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        _client = new HttpClient(handler, disposeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static TelegramHttpClient CreateDefault(NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = options.TelegramConnectTimeout,
            AllowAutoRedirect = false
        };
        return new TelegramHttpClient(handler, options, disposeHandler: true);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public void Dispose() => _client.Dispose();
}

public sealed class TelegramNotificationChannel(
    NotificationOptions options,
    TelegramHttpClient httpClient,
    TelegramRateLimiter rateLimiter,
    TimeProvider timeProvider) : INotificationChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Code => NotificationChannels.Telegram;

    public async Task SendAsync(NotificationMessage notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!options.IsTelegramConfigured)
            throw new NotificationDeliveryException("telegram_not_configured");

        var chatId = notification.Recipient?.Trim();
        if (string.IsNullOrWhiteSpace(chatId) || chatId.Any(char.IsControl))
            throw new NotificationDeliveryException("telegram_invalid_recipient");

        var text = string.IsNullOrWhiteSpace(notification.HtmlBody) ? notification.TextBody : notification.HtmlBody;
        if (string.IsNullOrWhiteSpace(text) || text.Length > NotificationContracts.TelegramMaxMessageLength)
            throw new NotificationDeliveryException("telegram_invalid_payload");

        await rateLimiter.WaitTurnAsync(NotificationContracts.HashRecipient(chatId), cancellationToken);

        var baseUri = new Uri(options.TelegramBaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var endpoint = new Uri(baseUri, $"bot{options.TelegramBotToken.Trim()}/sendMessage");

        using var timeoutCts = new CancellationTokenSource(options.TelegramRequestTimeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                chat_id = chatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            }, options: JsonOptions)
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NotificationDeliveryException("telegram_timeout");
        }
        catch (HttpRequestException)
        {
            // Never attach HttpRequestException: it may contain the request URI with bot token.
            throw new NotificationDeliveryException("telegram_network_error");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new NotificationDeliveryException("telegram_rate_limited");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new NotificationDeliveryException("telegram_auth_failed");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new NotificationDeliveryException("telegram_bad_request");
            if ((int)response.StatusCode >= 500)
                throw new NotificationDeliveryException("telegram_server_error");
            if (!response.IsSuccessStatusCode)
                throw new NotificationDeliveryException("telegram_http_error");

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: linkedCts.Token);
                if (!document.RootElement.TryGetProperty("ok", out var okProp)
                    || okProp.ValueKind != JsonValueKind.True)
                    throw new NotificationDeliveryException("telegram_api_rejected");
            }
            catch (NotificationDeliveryException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new NotificationDeliveryException("telegram_timeout");
            }
            catch (Exception)
            {
                throw new NotificationDeliveryException("telegram_invalid_response");
            }
        }
    }
}
