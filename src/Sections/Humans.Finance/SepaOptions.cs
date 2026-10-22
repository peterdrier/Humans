using Humans.Finance.Models;

namespace Humans.Finance;

/// <summary>
/// "Creditor" is the historical name of these <c>Sepa:*</c> settings; in a pain.001 payout the organisation is the
/// <em>debtor</em>, and that is where they land in the file.
/// </summary>
internal sealed class SepaOptions
{
    /// <summary><c>Dbtr/Nm</c>.</summary>
    public string? CreditorName { get; set; }

    /// <summary><c>DbtrAcct/Id/IBAN</c>: the account the payout leaves from.</summary>
    public string? CreditorIban { get; set; }

    /// <summary>Optional. <c>DbtrAgt/FinInstnId/BICFI</c>.</summary>
    public string? CreditorBic { get; set; }

    /// <summary>NIF + 3-char suffix, <c>InitgPty/Id/OrgId/Othr/Id</c>. Never inferred: Sabadell rejects a file
    /// presented under an id it did not issue.</summary>
    public string? CreditorIdentifier { get; set; }

    /// <summary>Only the prefill on <c>/Finance/Creditors</c>: the posted per-batch value is what is enforced.</summary>
    public decimal MaxPayoutPerTransfer { get; set; } = 50m;

    /// <summary><c>treasury_id</c> on <c>POST /purchases/{id}/payments</c>. Never inferred: Holded would fall back to
    /// the account default, not necessarily the bank the file drew on. Unset means no Book buttons.</summary>
    public string TreasuryAccountId { get; set; } = "";

    /// <summary>Sabadell's <c>572xxxxx</c>: the credit side of the journal entry that settles what open documents do
    /// not cover. Never inferred, for the same reason. Unset means no Book buttons.</summary>
    public int? TreasuryLedgerAccount { get; set; }

    public SepaPayoutSettings GetPayoutSettings()
    {
        // Never inferred: a file presented under a guessed name, account or presenter id is rejected
        // by the bank at best, and paid out of the wrong account at worst.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(CreditorName)) missing.Add("Sepa:CreditorName");
        if (string.IsNullOrWhiteSpace(CreditorIban)) missing.Add("Sepa:CreditorIban");
        if (string.IsNullOrWhiteSpace(CreditorIdentifier)) missing.Add("Sepa:CreditorIdentifier");

        return new SepaPayoutSettings(
            MaxPayoutPerTransfer,
            missing.Count == 0
                ? null
                : $"SEPA payout is unavailable — not configured: {string.Join(", ", missing)}.");
    }
}
