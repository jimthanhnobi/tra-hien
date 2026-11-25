namespace TeaPremium.Application.Notifications;

// 02-architecture.md §5.6: external notification delivery port (telegram, email; future sms/zns).
public sealed record NotificationMessage(
    string Channel,
    string Template,
    string Recipient,
    string? Subject,
    string TextBody,
    string? HtmlBody,
    string? RefType,
    Guid? RefId);

public interface INotificationChannel
{
    string Code { get; }
    Task SendAsync(NotificationMessage notification, CancellationToken cancellationToken);
}

// Fixed safe ASCII error code only: never carries secrets, PII, URIs or inner provider exceptions.
public sealed class NotificationDeliveryException : Exception
{
    public string ErrorCode { get; }

    public NotificationDeliveryException(string errorCode) : base(SanitizeCode(errorCode))
    {
        ErrorCode = Message;
    }

    private static string SanitizeCode(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)
            || errorCode.Length > 64
            || errorCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            return "notification_delivery_failed";
        return errorCode;
    }
}
