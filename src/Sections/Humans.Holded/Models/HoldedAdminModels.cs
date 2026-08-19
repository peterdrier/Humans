using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Models;

/// <summary>The /Holded page: this section's overview plus the two things only Finance knows —
/// how its purchase-doc sync is doing and how many creditor bindings exist.</summary>
/// <param name="CreditorBindings">Repo-backed count from Finance — no live Holded read.</param>
internal sealed record HoldedOverviewVm(
    HoldedAdminOverview Overview, HoldedDocSyncInfo DocSync, int? CreditorBindings);

/// <summary>
/// Everything the /Holded screen shows about the mirror itself. Assembled from the cache plus
/// one live call (GET /usage); Finance's doc-sync row and creditor-binding count are joined on
/// by the controller, because the Holded section never references Finance.
/// </summary>
internal sealed record HoldedAdminOverview(
    bool ApiReachable,
    HoldedUsageDto? Usage,
    int MonthlyCallBudget,
    IReadOnlyList<HoldedMonthlyCalls> CallsByMonth,
    IReadOnlyList<HoldedSyncStateRow> SyncStates,
    IReadOnlyList<HoldedAccountRow> Accounts,
    int LedgerLineCount);

/// <summary>Metered API calls for one Madrid-zone month.</summary>
internal sealed record HoldedMonthlyCalls(
    int Year, int Month, int Total, IReadOnlyDictionary<string, int> ByEndpoint);

internal sealed record HoldedSyncStateRow(
    string Kind, string Status, Instant? LastSyncAt, string? LastError, int LastCount);

/// <summary>One chart account on the /Holded reconciliation table.</summary>
/// <param name="Group">The PGC group's English name, derived from the account number's leading
/// digit — not Holded's own <c>group</c> string, which is Spanish ("Existencias", "Activo no
/// corriente"). The number is the authority: it is what the group means.</param>
/// <param name="LocalBalance">Null when the mirror holds no line for the account — distinct
/// from a cached zero, which reconciles a zero Holded balance.</param>
/// <param name="HoldedHasPostings">Holded's own debit or credit total is non-zero. Not derivable
/// from <paramref name="HoldedBalance"/>: an account with equal debits and credits — a clearing
/// account, a bank drained to nothing — nets to zero while having been posted to all year.</param>
internal sealed record HoldedAccountRow(
    int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled,
    bool HoldedHasPostings);

/// <summary>
/// One general-ledger account and every cached line on it, in the association's own point of
/// view: + means its money went up, − means it went down (see <see cref="HoldedStatementLine"/>).
/// Finance inverts to the user's POV for its own pages; this one never does.
/// </summary>
/// <param name="Lines">Date first, then entry/line — the order the daybook was written in.</param>
internal sealed record HoldedAccountStatement(
    HoldedAccountRow Account, IReadOnlyList<HoldedStatementLine> Lines);

/// <summary>The opposing leg(s) of a ledger entry, resolved for display next to a statement
/// line. When more than one opposing account exists (a purchase invoice: expense + VAT +
/// creditor), <see cref="AccountNum"/>/<see cref="AccountName"/> name the largest by absolute
/// amount and <see cref="TotalOpposingLines"/> lets the view render "+N more" and link to the
/// entry page instead of the single account page.</summary>
internal sealed record HoldedCounterparty(int AccountNum, string? AccountName, int TotalOpposingLines);

/// <summary>One line on an account statement: the cached line's date/type/description, the
/// signed <see cref="Amount"/> in the association's own point of view (+ money in, − money
/// out), and the resolved <see cref="Counterparty"/>. Internal — this is a presentation shape
/// over <see cref="HoldedLedgerLineInfo"/>, never the cross-section contract itself.</summary>
internal sealed record HoldedStatementLine(
    int EntryNumber, int Line, Instant Date, string? Type, string? Description,
    decimal Amount, HoldedCounterparty? Counterparty);

/// <summary>One leg of a ledger entry on the /Holded/Entries/{number} page: account number/name,
/// type, description, and the signed Amount in the association's point of view. No balancing
/// total — see the route's doc comment.</summary>
internal sealed record HoldedEntryLine(
    int AccountNum, string? AccountName, string? Type, string? Description, decimal Amount);

/// <summary>Every leg of one Holded journal entry — /Holded/Entries/{number}.</summary>
internal sealed record HoldedEntry(int EntryNumber, IReadOnlyList<HoldedEntryLine> Lines);
