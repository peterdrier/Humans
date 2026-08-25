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
    IReadOnlyList<string> GroupIds) // IDs of groups this subscriber currently belongs to
{
    private static readonly HashSet<string> SuppressedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "unsubscribed", "bounced", "junk" };

    /// <summary>
    /// MailerLite will not deliver to this address, so no group may hold it. The one
    /// definition of that rule: the sync, the dashboard stats and the debug preview all read
    /// it here, so a preview can never disagree with the apply about who gets excluded.
    /// </summary>
    public bool IsSuppressed => SuppressedStatuses.Contains(Status);
}
