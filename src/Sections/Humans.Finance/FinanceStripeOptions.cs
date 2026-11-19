using System.Globalization;

namespace Humans.Finance;

/// <summary><c>Finance:StripeLedgerAccounts</c>: each Stripe gateway's Holded ledger account (441xxxxx) and the Stripe
/// account (<c>acct_…</c>) whose mirrored balance is its "At the source" figure on /Finance.</summary>
internal sealed class FinanceStripeOptions
{
    public Dictionary<string, string> StripeLedgerAccounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Throws on a key that is not a ledger account number: a typo must not hide an account.</summary>
    public IReadOnlyDictionary<int, string> ByLedgerAccount() => StripeLedgerAccounts.ToDictionary(
        kv => int.Parse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture), kv => kv.Value);
}
