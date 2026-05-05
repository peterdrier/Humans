using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreOrder
{
    public Guid Id { get; set; }

    /// <summary>
    /// Cross-section linkage to <c>CampSeason</c> — bare Guid, no EF navigation, no FK
    /// constraint (per <c>memory/architecture/no-cross-section-ef-joins.md</c>). Resolved
    /// at the service layer via <c>ICampService.GetCampSeasonByIdAsync</c>.
    /// </summary>
    public Guid CampSeasonId { get; set; }
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
