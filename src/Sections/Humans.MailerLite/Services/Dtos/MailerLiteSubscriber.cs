using NodaTime;

namespace Humans.MailerLite.Services.Dtos;

/// <summary>Engagement metrics and IP fields are left out on purpose: GDPR minimisation.</summary>
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
    IReadOnlyList<string> GroupIds) // IDs of groups this subscriber currently belongs to
{
    private static readonly HashSet<string> SuppressedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unsubscribed", "bounced", "junk" };

    /// <summary>
    /// The one definition of "MailerLite won't deliver here": sync, stats and the debug preview all read it, so a
    /// preview can't disagree with the apply.
    /// </summary>
    public bool IsSuppressed => SuppressedStatuses.Contains(Status);
}
