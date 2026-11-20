namespace Humans.Store.Domain;

/// <summary>One row (<see cref="SingletonId"/>), edited on /Settings#store. Both values are Acountax's call.</summary>
internal sealed class StoreSettings
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;

    /// <summary>No longer read or written: kept deposits are income and returned ones never reach the invoice. The
    /// column stays until a drop is approved.</summary>
    public int? DepositLiabilityAccountNum { get; set; }

    /// <summary>The Holded treasury account of the Store's Stripe gateway, whose feed carries each Store charge and
    /// refund. Unset, invoices are issued but not booked.</summary>
    public string? StripeGatewayTreasuryId { get; set; }

    /// <summary>Order total at or below which an order without counterparty details may get a factura simplificada.
    /// The law allows €400 generally and €3,000 for retail B2C; which applies is Acountax's call, so €400.</summary>
    public decimal SimplifiedInvoiceThresholdEur { get; set; } = 400m;
}
