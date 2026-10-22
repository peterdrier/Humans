using NodaTime;

namespace Humans.Consent.Domain;

/// <summary>
/// Replaces <c>users.LastConsentReminderSentAt</c>, which Users keeps unwritten until the
/// <c>consent.reminder-stamps</c> data migration is done in production.
/// </summary>
internal sealed class ConsentReminder
{
    public Guid UserId { get; init; }

    public Instant LastSentAt { get; set; }
}
