using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreOrder
{
    public Guid Id { get; set; }

    /// <summary>
    /// Cross-section linkage to <c>CampSeason</c> — bare Guid, no EF navigation, no FK
    /// constraint (per <c>memory/architecture/no-cross-section-ef-joins.md</c>). Resolved
    /// at the service layer via <c>ICampService.GetCampSeasonByIdAsync</c>. Exactly one of
    /// <see cref="CampSeasonId"/> and <see cref="TeamId"/> is non-null; the invariant is
    /// service-enforced, not DB-enforced.
    /// </summary>
    public Guid? CampSeasonId { get; set; }

    /// <summary>
    /// Cross-section linkage to <c>Team</c> for non-billable department orders — bare
    /// Guid, no EF navigation, no FK constraint. Resolved at the service layer via
    /// <c>ITeamServiceRead.GetTeamAsync</c>. Exactly one of <see cref="CampSeasonId"/>
    /// and <see cref="TeamId"/> is non-null; the invariant is service-enforced, not
    /// DB-enforced.
    /// </summary>
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Event year the order's catalog draws from. Always set on write. For camp orders
    /// this mirrors <c>CampSeason.Year</c>; for team orders it is the active event year
    /// at create time. Legacy rows may carry <c>0</c> until they're next saved through
    /// the service, at which point the camp-side year is backfilled.
    /// </summary>
    public int Year { get; set; }

    public string? Label { get; set; }
    public StoreOrderState State { get; set; } = StoreOrderState.Open;

    public string? CounterpartyName { get; set; }
    public string? CounterpartyVatId { get; set; }
    public string? CounterpartyAddress { get; set; }
    public string? CounterpartyCountryCode { get; set; }
    public string? CounterpartyEmail { get; set; }

    public Guid? IssuedInvoiceId { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<StoreOrderLine> Lines { get; set; } = new List<StoreOrderLine>();
    public ICollection<StorePayment> Payments { get; set; } = new List<StorePayment>();
}
