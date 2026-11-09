using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smzdm.Monitor;

public interface INotificationSender
{
    string DestinationKey { get; }
    Task<long> PublishAsync(string message, long? messageId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public sealed class TelegramDeliveryException(string message, int retryAfterSeconds = 60) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class TelegramSender(
    HttpClient client, TelegramOptions options, NotificationPolicy policy, TimeProvider clock) : INotificationSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private DateTimeOffset _nextSendAt;
    public string DestinationKey { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{options.BotToken}|{options.ChatId}|{options.MessageThreadId}")));

    public async Task<long> PublishAsync(string message, long? messageId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var wait = _nextSendAt - clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, cancellationToken);
            // Recheck after waits and before EVERY attempt, including internal retries.
            if (policy.IsQuiet(clock.GetUtcNow()) || clock.GetUtcNow() >= expiresAt)
                throw new TelegramDeliveryException("已进入静默时段或本条榜单已过期，暂停 Telegram 请求。");
            var retryAfter = 2 * (attempt + 1);
            try
            {
                var method = messageId.HasValue ? "editMessageText" : "sendMessage";
                using var response = await client.PostAsJsonAsync(
                    $"https://api.telegram.org/bot{options.BotToken}/{method}",
                    new
                    {
                        chat_id = options.ChatId,
                        text = message,
                        parse_mode = "HTML",
                        message_id = messageId,
                        message_thread_id = messageId.HasValue ? null : options.MessageThreadId,
                        link_preview_options = new { is_disabled = true }
                    }, JsonOptions, cancellationToken);
                _nextSendAt = clock.GetUtcNow().AddMilliseconds(1100);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
                if (response.IsSuccessStatusCode && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
                        result.TryGetProperty("message_id", out var id) && id.ValueKind == JsonValueKind.Number &&
                        id.TryGetInt64(out var returnedId) && returnedId > 0)
                        return returnedId;
                    throw new JsonException("Missing Telegram message_id.");
                }

                var code = root.TryGetProperty("error_code", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var errorCode)
                    ? errorCode : (int)response.StatusCode;
                // A retry after an acknowledged-but-unsaved edit is already applied.
                if (messageId.HasValue && code == 400 && root.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String &&
                    description.GetString()!.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                    return messageId.Value;
                if (code == 429)
                {
                    retryAfter = root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                        parameters.TryGetProperty("retry_after", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seconds)
                        ? Math.Clamp(seconds, 1, 86400) : 60;
                }
                else if (code < 500)
                    throw new TelegramDeliveryException($"Telegram 拒绝消息（错误码 {code}），请检查配置、原消息和机器人权限。");
                if (attempt == 2 || retryAfter > 60)
                    throw new TelegramDeliveryException($"Telegram 暂时不可用（错误码 {code}），稍后重试最新榜单。", retryAfter);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Telegram puts the token in the URI; never expose the original exception.
                if (attempt == 2)
                    throw new TelegramDeliveryException("Telegram 网络超时或响应无效，稍后重试最新榜单。");
            }
            await Task.Delay(TimeSpan.FromSeconds(retryAfter), clock, cancellationToken);
        }
        throw new TelegramDeliveryException("Telegram 未确认消息发送。");
    }
}
