using System.Text.Json.Serialization;
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

    /// <summary>
    /// The order's checkout intent is saved before Stripe is called. Its session is filled after acknowledgement;
    /// all checkout fields clear together when an accounted outcome or expiry closes that session.
    /// </summary>
    public Guid? CheckoutAttemptId { get; set; }
    public string? CheckoutSessionId { get; set; }
    public decimal? CheckoutAmountEur { get; set; }
    public StoreCheckoutIntent? CheckoutIntent { get; set; }

    public bool HasCheckoutAttempt =>
        CheckoutAttemptId is not null || CheckoutSessionId is not null || CheckoutAmountEur is not null || CheckoutIntent is not null;

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}

[method: JsonConstructor]
internal sealed record StoreCheckoutIntent(
    decimal AmountEur, string ReturnUrl, string? CustomerEmail, string Description,
    string? SupersededSessionId, long CreatedAtUnix);
