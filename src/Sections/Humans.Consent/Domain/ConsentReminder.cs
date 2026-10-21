using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// When the re-consent reminder email last went to a member, one row per member
/// (<c>consent_reminders</c>): the cooldown anchor <c>SendReConsentReminderJob</c> reads and
/// stamps. Replaces <c>users.LastConsentReminderSentAt</c>, which Users keeps unwritten until the
/// <c>consent.reminder-stamps</c> data migration is done in production.
/// </summary>
internal sealed class ConsentReminder
{
    /// <summary>The member. Bare id, no FK to <c>users</c>
    /// (memory/architecture/no-cross-section-ef-joins.md).</summary>
    public Guid UserId { get; init; }

    /// <summary>When the last reminder was sent.</summary>
    public Instant LastSentAt { get; set; }
}
