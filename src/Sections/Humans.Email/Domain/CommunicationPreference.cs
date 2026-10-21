using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Email.Domain;

/// <summary>
/// A member's opt-in/opt-out preference for one message category (<c>email_communication_preferences</c>,
/// Users decomposition commit 14). One row per user per category; CAN-SPAM/GDPR compliance. Bare
/// <see cref="UserId"/>, no FK: <c>users</c> is Users' table.
/// </summary>
internal sealed class CommunicationPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public MessageCategory Category { get; init; }

    /// <summary>
    /// True when the member opted out of email for the category. Null marks a cleared row, written
    /// before the <c>email.communication-preferences</c> copy was retired to shadow a frozen legacy
    /// Users row. Nothing writes one now; reads treat it as nothing recorded.
    /// </summary>
    public bool? OptedOut { get; set; }

    /// <summary>
    /// Whether in-app inbox notifications are enabled for this category.
    /// Default true. When false, informational notifications for this category are suppressed.
    /// Actionable notifications are always shown regardless of this setting.
    /// </summary>
    public bool InboxEnabled { get; set; } = true;

    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// How this preference was set: "Profile", "MagicLink", "DataMigration", etc.
    /// </summary>
    public string UpdateSource { get; set; } = string.Empty;

    /// <summary>
    /// Earliest opt-in instant we know about for this category. Stamped on the
    /// first opt-in transition (OptedOut: true → false) or on the first import
    /// from an external source that carries a real subscribe date. Preserved
    /// across opt-out / re-opt cycles — represents "first ever subscribed",
    /// not "currently subscribed since". Null for rows that pre-date the column
    /// or were lazy-seeded as default-opted-out.
    /// </summary>
    public Instant? SubscribedAt { get; set; }
}
