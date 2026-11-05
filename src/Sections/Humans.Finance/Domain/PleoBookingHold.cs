using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>A Pleo entry posted (or possibly posted) to Holded 57200003 that the ledger has not shown yet. Written
/// before the post, deleted once a re-pull shows it (or Holded refused it, or a human released it after checking
/// Holded). Durable, so a restart can never turn an unconfirmed post back into a ready one. Not a Holded id: the
/// ledger stays the record of what was booked.</summary>
internal sealed class PleoBookingHold
{
    public string PleoEntryId { get; init; } = "";
    public Instant HeldAt { get; init; }
}
