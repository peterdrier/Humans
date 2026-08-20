using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

/// <summary>
/// Read-only projection of a MailerLite subscriber row. Excludes engagement
/// metrics and IP fields by design — GDPR scope minimisation.
/// </summary>
internal sealed record MailerLiteSubscriber(
    string Id,
    string Email,
    string Status,            // "active" | "unsubscribed" | "unconfirmed" | "bounced" | "junk"
    string Source,            // "manual" | "api" | "form" | ...
    Instant? SubscribedAt,    // UTC; null for unconfirmed
    Instant? UnsubscribedAt,  // UTC; null when not unsubscribed
    Instant? OptedInAt,       // UTC; null until double-opt-in confirmed
    string? FirstName,
    string? LastName,
    IReadOnlyList<string> GroupIds); // IDs of groups this subscriber currently belongs to
