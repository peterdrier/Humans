namespace Humans.Store;

/// <summary>
/// Section-owned settings, bound in <see cref="Section.Register"/> from <c>Store:*</c>.
/// Both values are Acountax's call and change without a code deploy, which is why they are
/// configuration rather than constants.
/// </summary>
internal sealed class StoreSectionOptions
{
    public const string Section = "Store";

    /// <summary>
    /// Holded chart number of the refundable-deposit liability account (<i>fianzas recibidas</i>).
    /// Deposits are not income: they post as tax-0 lines to this account until refunded or
    /// forfeited. Null until Acountax names it — issuing an invoice that carries a deposit line
    /// is refused while it is unset rather than booking the deposit as revenue.
    /// </summary>
    public int? DepositLiabilityAccountNum { get; set; }

    /// <summary>
    /// Order total (incl. VAT and deposits) at or below which an order with no counterparty
    /// details may be issued as a <i>factura simplificada</i>. Spanish law allows €400 generally
    /// and €3,000 for retail-type B2C supplies; which category store items fall under is
    /// Acountax's call, so the default is the conservative €400.
    /// </summary>
    public decimal SimplifiedInvoiceThresholdEur { get; set; } = 400m;
}
