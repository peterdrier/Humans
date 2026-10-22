using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Email.Domain;

internal sealed class CommunicationPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public MessageCategory Category { get; init; }

    /// <summary>
    /// Null only for a reset that shadows a legacy Users row, so the read fallback can't bring the old value
    /// back. A reset with no legacy row deletes the row instead.
    /// </summary>
    public bool? OptedOut { get; set; }

    /// <summary>When false, informational notifications in this category are hidden; actionable ones always show.</summary>
    public bool InboxEnabled { get; set; } = true;

    public Instant UpdatedAt { get; set; }

    /// <summary>For example "Profile", "MagicLink", "DataMigration".</summary>
    public string UpdateSource { get; set; } = string.Empty;

    /// <summary>
    /// First opt-in ever known, kept across later opt-outs: "first subscribed", not "subscribed since".
    /// Null for rows older than the column or seeded as opted out.
    /// </summary>
    public Instant? SubscribedAt { get; set; }
}
