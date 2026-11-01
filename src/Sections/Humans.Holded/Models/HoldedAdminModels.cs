using Humans.Holded.Contracts;
using NodaTime;

namespace Humans.Holded.Models;

/// <summary>/Holded. <paramref name="FullSyncCalls"/> estimates a full resync's cost: one call per 200-line page plus
/// the chart.</summary>
internal sealed record HoldedSummary(
    bool IsConfigured,
    bool ApiReachable,
    HoldedUsageDto? Usage,
    int MonthlyCallBudget,
    IReadOnlyList<HoldedSyncStateRow> SyncStates,
    int LedgerLineCount,
    int EntryCount,
    int AccountCount,
    int MismatchCount,
    int FullSyncCalls);

internal sealed record HoldedMonthlyCalls(
    int Year, int Month, int Total, IReadOnlyDictionary<string, int> ByEndpoint);

internal sealed record HoldedSyncStateRow(
    string Kind, string Status, Instant? LastSyncAt, string? LastError, int LastCount);

/// <summary>The /Holded/Accounts query string. Archived and empty accounts are hidden unless asked for;
/// <paramref name="Mismatched"/> narrows to the accounts where the mirror disagrees with Holded.</summary>
internal sealed record HoldedAccountsFilter(bool ShowArchived = false, bool ShowEmpty = false, bool Mismatched = false);

internal sealed record HoldedAccountsPage(
    HoldedAccountsFilter Filter,
    IReadOnlyList<HoldedAccountGroup> Groups,
    int HiddenArchived,
    int HiddenEmpty);

/// <summary>One PGC group (leading digit). Totals are over the listed accounts, in the association's POV.</summary>
internal sealed record HoldedAccountGroup(
    int Digit, string Name, IReadOnlyList<HoldedAccountRow> Accounts,
    decimal HoldedBalance, decimal MirrorBalance, int LineCount, int Mismatches);

/// <summary>
/// <paramref name="Group"/> comes from the number's leading digit, not Holded's Spanish <c>group</c> string.
/// A null <paramref name="LocalBalance"/> means no cached line, unlike a cached zero. <paramref name="HoldedHasPostings"/>
/// isn't derivable from <paramref name="HoldedBalance"/>: a clearing account nets to zero after a year of postings.
/// <paramref name="EnglishName"/> is null when Holded's name is not the standard PGC one.
/// </summary>
internal sealed record HoldedAccountRow(
    int Number, string Name, string? Group,
    decimal HoldedBalance, decimal? LocalBalance, int LocalLineCount, bool Reconciled,
    bool HoldedHasPostings, string? EnglishName = null, bool Archived = false)
{
    /// <summary>Hidden by default: no cached line, nothing at Holded, and no disagreement. "No lines" alone would
    /// hide both missing-data alarms: a Holded balance the mirror has no line for, and postings netting to zero.</summary>
    public bool IsEmpty => LocalLineCount == 0 && !HoldedHasPostings && Reconciled;
}

/// <summary>
/// In the association's point of view (+ its money went up); Finance flips to the member's for its own pages.
/// <paramref name="Lines"/> are date first, then entry/line: daybook order.
/// </summary>
internal sealed record HoldedAccountStatement(
    HoldedAccountRow Account, IReadOnlyList<HoldedStatementLine> Lines);

/// <summary>When there are several opposing accounts (expense + VAT + creditor), names the largest by absolute amount;
/// <see cref="TotalOpposingLines"/> lets the view render "+N more" linking to the entry page.</summary>
internal sealed record HoldedCounterparty(int AccountNum, string? AccountName, int TotalOpposingLines);

/// <summary><see cref="Amount"/> is in the association's point of view (+ money in, − money out). A null
/// <see cref="Checked"/> is a line not re-synced since Humans began reading the tick.</summary>
internal sealed record HoldedStatementLine(
    int EntryNumber, int Line, Instant Date, string? Type, string? Description,
    decimal Amount, HoldedCounterparty? Counterparty, string? EntryId = null, bool? Checked = null);

/// <summary>Amount is in the association's point of view. No balancing total.</summary>
internal sealed record HoldedEntryLine(
    int AccountNum, string? AccountName, string? Type, string? Description, decimal Amount, bool? Checked = null);

/// <summary><paramref name="EntryId"/> is Holded's id for the entry, null until a sync has read it.</summary>
internal sealed record HoldedEntry(int EntryNumber, string? EntryId, IReadOnlyList<HoldedEntryLine> Lines);
