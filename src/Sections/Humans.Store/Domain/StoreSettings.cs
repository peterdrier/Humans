namespace Humans.Store.Domain;

/// <summary>One row (<see cref="SingletonId"/>), edited on /Settings#store. Both values are Acountax's call.</summary>
internal sealed class StoreSettings
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;

    /// <summary>The refundable-deposit liability account (<i>fianzas recibidas</i>). While null, issuing an invoice
    /// with a deposit line is refused rather than booking the deposit as revenue.</summary>
    public int? DepositLiabilityAccountNum { get; set; }

    /// <summary>Order total at or below which an order without counterparty details may get a factura simplificada.
    /// The law allows €400 generally and €3,000 for retail B2C; which applies is Acountax's call, so €400.</summary>
    public decimal SimplifiedInvoiceThresholdEur { get; set; } = 400m;
}
