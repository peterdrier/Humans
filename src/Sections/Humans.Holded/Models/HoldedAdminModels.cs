using Humans.Finance.Contracts;
using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Models;

internal sealed record HoldedOverviewVm(HoldedAdminOverview Overview, HoldedDocSyncInfo DocSync);

/// <summary>The cache plus one live call (GET /usage). The controller joins Finance's parts on: this service reads
/// nothing of Finance's.</summary>
internal sealed record HoldedAdminOverview(
    bool ApiReachable,
    HoldedUsageDto? Usage,
    int MonthlyCallBudget,
    IReadOnlyList<HoldedMonthlyCalls> CallsByMonth,
    IReadOnlyList<HoldedSyncStateRow> SyncStates,
    IReadOnlyList<HoldedAccountRow> Accounts,
    int LedgerLineCount);

internal sealed record HoldedMonthlyCalls(
    int Year, int Month, int Total, IReadOnlyDictionary<string, int> ByEndpoint);

internal sealed record HoldedSyncStateRow(
    string Kind, string Status, Instant? LastSyncAt, string? LastError, int LastCount);

/// <summary>
/// <paramref name="Group"/> comes from the number's leading digit, not Holded's Spanish <c>group</c> string.
/// A null <paramref name="LocalBalance"/> means no cached line, unlike a cached zero. <paramref name="HoldedHasPostings"/>
/// isn't derivable from <paramref name="HoldedBalance"/>: a clearing account nets to zero after a year of postings.
/// </summary>
internal sealed record HoldedAccountRow(
    int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled,
    bool HoldedHasPostings);

/// <summary>
/// In the association's point of view (+ its money went up); Finance flips to the member's for its own pages.
/// <paramref name="Lines"/> are date first, then entry/line: daybook order.
/// </summary>
internal sealed record HoldedAccountStatement(
    HoldedAccountRow Account, IReadOnlyList<HoldedStatementLine> Lines);

/// <summary>When there are several opposing accounts (expense + VAT + creditor), names the largest by absolute amount;
/// <see cref="TotalOpposingLines"/> lets the view render "+N more" linking to the entry page.</summary>
internal sealed record HoldedCounterparty(int AccountNum, string? AccountName, int TotalOpposingLines);

/// <summary><see cref="Amount"/> is in the association's point of view (+ money in, − money out).</summary>
internal sealed record HoldedStatementLine(
    int EntryNumber, int Line, Instant Date, string? Type, string? Description,
    decimal Amount, HoldedCounterparty? Counterparty);

/// <summary>Amount is in the association's point of view. No balancing total.</summary>
internal sealed record HoldedEntryLine(
    int AccountNum, string? AccountName, string? Type, string? Description, decimal Amount);

internal sealed record HoldedEntry(int EntryNumber, IReadOnlyList<HoldedEntryLine> Lines);
