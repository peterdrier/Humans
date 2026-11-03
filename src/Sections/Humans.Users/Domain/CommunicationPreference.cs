using NodaTime;


namespace Humans.Users.Domain;

/// <summary>Frozen storage only; Email owns preferences. Kept until the separate table drop.</summary>
internal sealed class CommunicationPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public LegacyMessageCategory Category { get; init; }

    public bool OptedOut { get; set; }

    /// <summary>When false, informational inbox notifications are suppressed; actionable ones always show.</summary>
    public bool InboxEnabled { get; set; } = true;

    public Instant UpdatedAt { get; set; }

    /// <summary>"Profile", "MagicLink", "DataMigration", etc.</summary>
    public string UpdateSource { get; set; } = string.Empty;

    /// <summary>
    /// First-ever opt-in, kept across opt-out/re-opt cycles. Null for rows that predate the column
    /// or were lazy-seeded opted out.
    /// </summary>
    public Instant? SubscribedAt { get; set; }

}
