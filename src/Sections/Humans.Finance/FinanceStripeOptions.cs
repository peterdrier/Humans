using System.Globalization;

namespace Humans.Finance;

/// <summary><c>Finance:StripeLedgerAccounts</c>: each Stripe gateway's Holded ledger account (441xxxxx) and the Stripe
/// account (<c>acct_…</c>) whose mirrored balance is its "At the source" figure on /Finance.</summary>
internal sealed class FinanceStripeOptions
{
    public Dictionary<string, string> StripeLedgerAccounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Null when any key is not a ledger account number or two keys name the same one: a typo must not
    /// hide an account, and must not take the rest of /Finance down either.</summary>
    public IReadOnlyDictionary<int, string>? ByLedgerAccount()
    {
        var links = new Dictionary<int, string>();
        foreach (var (key, accountId) in StripeLedgerAccounts)
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
                || !links.TryAdd(num, accountId))
                return null;
        return links;
    }
}
