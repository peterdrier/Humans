using NodaTime;

namespace Humans.Application.Interfaces.Mailer.Dtos;

/// <summary>
/// Read-only projection of a MailerLite subscriber row. Excludes engagement
/// metrics and IP fields by design — GDPR scope minimisation.
/// </summary>
public sealed record MailerLiteSubscriber(
    string Id,
    string Email,
    string Status,            // "active" | "unsubscribed" | "unconfirmed" | "bounced" | "junk"
    string Source,            // "manual" | "api" | "form" | ...
    Instant? SubscribedAt,    // UTC; null for unconfirmed
    Instant? UnsubscribedAt,  // UTC; null when not unsubscribed
    Instant? OptedInAt,       // UTC; null until double-opt-in confirmed
    string? FirstName,
    string? LastName);
