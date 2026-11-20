namespace TeaPremium.Application.Outbox;

// 02 §5.8 / BR-SEO-34: delivery is at-least-once; the receiver must treat repeated tags idempotently.
public sealed record StorefrontInvalidate(string[] Tags)
{ public const string Message = "StorefrontInvalidate"; }
