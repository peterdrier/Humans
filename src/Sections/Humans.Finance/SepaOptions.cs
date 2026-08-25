namespace Humans.Finance;

/// <summary>
/// The organisation's own SEPA identity, bound in <see cref="Section.Register"/> from the existing
/// <c>Sepa:*</c> keys. "Creditor" is the historical naming of these settings; in a pain.001 payout
/// the organisation is the <em>debtor</em>, and that is where they land in the file.
/// </summary>
internal sealed class SepaOptions
{
    /// <summary>Legal name of the organisation — <c>Dbtr/Nm</c>.</summary>
    public string? CreditorName { get; set; }

    /// <summary>The account the payout leaves from — <c>DbtrAcct/Id/IBAN</c>.</summary>
    public string? CreditorIban { get; set; }

    /// <summary>Optional BIC of the debtor's bank — <c>DbtrAgt/FinInstnId/BICFI</c>.</summary>
    public string? CreditorBic { get; set; }

    /// <summary>Presenter identifier (NIF + 3-char suffix) — <c>InitgPty/Id/OrgId/Othr/Id</c>.
    /// Never inferred: Sabadell rejects a file presented under an id it did not issue.</summary>
    public string? CreditorIdentifier { get; set; }

    /// <summary>Prefill default for the per-transfer cap on <c>/Finance/Creditors</c>. The admin can
    /// change it per batch; the posted value, not this one, is what <see cref="Services.Service.GenerateSepaPayoutAsync"/>
    /// enforces.</summary>
    public decimal MaxPayoutPerTransfer { get; set; } = 50m;

    /// <summary>The Holded treasury account a booked payout is paid from — <c>treasury_id</c> on
    /// <c>POST /purchases/{id}/payments</c>. Never inferred: Holded would otherwise fall back to the
    /// account default, which is not necessarily the bank account the SEPA file drew on. Unset means
    /// <c>/Finance/Sepa</c> says so instead of offering Book buttons.</summary>
    public string TreasuryAccountId { get; set; } = "";
}
