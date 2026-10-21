using NodaTime;


namespace Humans.Users.Domain;

/// <summary>
/// The frozen legacy <c>communication_preferences</c> row. Email owns the live preferences in
/// <c>email_communication_preferences</c> since Users decomposition commit 14; this table is no longer
/// written except by erasure, is read only by Users' GDPR export since the
/// <c>email.communication-preferences</c> copy was retired, and is dropped by USERS-20.
/// </summary>
internal sealed class CommunicationPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public LegacyMessageCategory Category { get; init; }

    public bool OptedOut { get; set; }

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
    /// not "currently subscribed since". Null for pre-existing rows that
    /// pre-date the column or for rows that were lazy-seeded as default-opted-out.
    /// </summary>
    public Instant? SubscribedAt { get; set; }

}
