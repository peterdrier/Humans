using Humans.Store.Contracts;
using NodaTime;

namespace Humans.Store.Domain;

internal sealed class Order
{
    public Guid Id { get; set; }

    /// <summary>Exactly one of this and <see cref="TeamId"/> is non-null; the service enforces it, not the DB.</summary>
    public Guid? CampSeasonId { get; set; }

    /// <summary>Non-billable department orders.</summary>
    public Guid? TeamId { get; set; }

    /// <summary>
    /// Always set on write: the camp season's year, or the active year at create for team orders. Legacy rows at
    /// <c>0</c> are repaired by the operator screen or the next add-line write; reads never repair.
    /// </summary>
    public int Year { get; set; }

    public OrderState State { get; set; } = OrderState.Open;

    public string? CounterpartyName { get; set; }
    public string? CounterpartyVatId { get; set; }
    public string? CounterpartyAddress { get; set; }
    public string? CounterpartyCountryCode { get; set; }
    public string? CounterpartyEmail { get; set; }

    public Guid? IssuedInvoiceId { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
