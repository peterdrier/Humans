namespace Humans.Finance.Domain;

/// <summary>A Stripe gateway's Holded ledger account (441xxxxx) and the Stripe account (<c>acct_…</c>) whose mirrored
/// balance is its "At the source" figure on /Finance. Edited on /Settings#finance.</summary>
internal sealed class StripeLedgerLink
{
    public int LedgerAccountNum { get; init; }
    public string StripeAccountId { get; set; } = string.Empty;
}
