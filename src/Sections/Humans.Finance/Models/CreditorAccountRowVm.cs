namespace Humans.Finance.Models;

/// <summary>More than one binding is a collision, and the row shows it rather than picking a winner.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="Balance"/> is inverted for display (credit − debit, positive = owed to the member). Everything
/// else keeps Holded's Σdebit − Σcredit; the flip happens here and nowhere else.</item>
/// <item><paramref name="OpenDocs"/> is null (shown as "—") on unbound and colliding rows, or when Holded's document
/// list could not be read.</item>
/// <item><paramref name="PendingPayoutFileName"/>: an unbooked, unwithdrawn transfer to this account; the row is not
/// payable again until it is booked or withdrawn.</item>
/// <item><paramref name="HasDuplicateDocuments"/> is null when the check could not run.</item>
/// </list>
/// </remarks>
internal sealed record CreditorAccountRowVm(
    int SupplierAccountNum,
    string Name,
    decimal? Balance,
    IReadOnlyList<CreditorBindingVm> Bindings,
    string? IbanMasked = null,
    int? OpenDocs = null,
    string? PendingPayoutFileName = null,
    bool? HasDuplicateDocuments = false)
{
    public const string DuplicateDocumentsReason = "duplicate documents in Holded";

    /// <summary>Needs an admin to unbind all but the owner.</summary>
    public bool HasCollision => Bindings.Count > 1;

    /// <summary>Null when payable. Display only: the server re-derives the same rules at generation.</summary>
    public string? NotPayableReason => Bindings.Count switch
    {
        0 => "unbound",
        > 1 => "collision",
        _ when HasDuplicateDocuments is null => "duplicate check unavailable — Holded's document list could not be read",
        _ when HasDuplicateDocuments == true => DuplicateDocumentsReason,
        _ when PendingPayoutFileName is { } file => $"in payout file {file}, not booked yet",
        _ when Balance is not > 0m => "nothing owed",
        _ when IbanMasked is null => "no IBAN in Holded",
        _ => null,
    };

    public bool IsPayable => NotPayableReason is null;

    /// <summary>Unbound rows sort last: an admin sorting by member is looking for people, not gaps.</summary>
    public string MemberSortKey => Bindings.Count == 0
        ? "￿"
        : string.Join(", ", Bindings.Select(b => b.MemberName)).ToUpperInvariant();
}

internal sealed record CreditorBindingVm(Guid UserId, string MemberName, string Source);

/// <summary>
/// <paramref name="Unresolved"/> is the bindings with no 400000xx. Nothing retries their resolution, so this is the
/// only place they can be bound. <paramref name="SortBy"/> is "account" (default), "name", "balance", "opendocs" or
/// "member".
/// </summary>
internal sealed record CreditorsPageVm(
    IReadOnlyList<CreditorAccountRowVm> Accounts,
    IReadOnlyList<CreditorBindingVm> Unresolved,
    string SortBy,
    string SortDir,
    SepaPayoutSettings Sepa,
    int? ReconProblems = 0)
{
    /// <summary>Null when the expense reconciliation cannot be computed (the document mirror never synced).</summary>
    public bool ReconUnavailable => ReconProblems is null;

    /// <summary>Zero means the check ran and found nothing.</summary>
    public int DuplicateFlaggedAccounts => Accounts.Count(a => a.HasDuplicateDocuments == true);

    /// <summary>Holded's document list was unreadable for at least one bound row.</summary>
    public bool DuplicateCheckUnavailable => Accounts.Any(a => a.HasDuplicateDocuments is null);
}

/// <summary>The failure message is admin-facing.</summary>
internal sealed record CreditorBindResult(bool Succeeded, string? ErrorMessage)
{
    public static CreditorBindResult Success { get; } = new(true, null);

    public static CreditorBindResult Failure(string message) => new(false, message);
}
