using NodaTime;

namespace Humans.Consent.Domain;

internal sealed class ConsentReminder
{
    public Guid UserId { get; init; }

    public Instant LastSentAt { get; set; }
}
