using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// The frozen legacy <c>communication_preferences</c> row; Email owns the live preferences. Written
/// only by erasure, read into the obsolete <see cref="UserInfo.CommunicationPreferences"/>, and
/// dropped once <c>email.communication-preferences</c> reports done.
/// </summary>
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
