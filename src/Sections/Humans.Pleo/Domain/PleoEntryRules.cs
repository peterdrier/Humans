namespace Humans.Pleo.Domain;

internal enum PleoEntryKind { Load, CardPurchase, PleoInvoice, OutOfPocket, Unclassified }

internal static class PleoEntryRules
{
    public const string Eur = "EUR";

    /// <summary>Only the family/subFamily pairs seen on the live API are classified; anything new is
    /// <see cref="PleoEntryKind.Unclassified"/> until someone decides what it is.</summary>
    public static PleoEntryKind KindOf(string? family, string? subFamily) => (family, subFamily) switch
    {
        ("WALLET", "LOAD") => PleoEntryKind.Load,
        ("CARD_PURCHASE", "IN_STORE" or "ONLINE") => PleoEntryKind.CardPurchase,
        ("PLEO_INVOICE", "PLEO_INVOICE" or null) => PleoEntryKind.PleoInvoice,
        ("OUT_OF_POCKET", null) => PleoEntryKind.OutOfPocket,
        _ => PleoEntryKind.Unclassified,
    };

    /// <summary>
    /// <c>totalBillValue</c> is signed and in the company currency, so it wins whenever it is EUR: card refunds
    /// arrive as <c>CARD_PURCHASE</c> with a positive bill value, and USD purchases carry Pleo's own conversion.
    /// Without it, an EUR <c>transactionValue</c> (always unsigned) takes its sign from the family; an
    /// unclassified family is never guessed.
    /// </summary>
    public static decimal? SignedEur(
        string? family, string? subFamily, string currency, long minors, string? billCurrency, long? billMinors)
    {
        if (string.Equals(billCurrency, Eur, StringComparison.Ordinal) && billMinors is { } bill) return bill / 100m;
        if (!string.Equals(currency, Eur, StringComparison.Ordinal)) return null;
        return KindOf(family, subFamily) switch
        {
            PleoEntryKind.Load => minors / 100m,
            PleoEntryKind.Unclassified => null,
            _ => -minors / 100m,
        };
    }

    /// <summary>Pending rows are listed but never counted: Pleo's own current balance leaves them out too.</summary>
    public static bool IsCompleted(string? status) => string.Equals(status, "COMPLETED", StringComparison.Ordinal);
}
