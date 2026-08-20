using Humans.Finance.Domain;
using NodaTime;

namespace Humans.Finance.Models;

/// <summary>
/// The <c>/Finance/Holded</c> read model: the Finance-owned half of the Holded connector that had
/// no screen at all (nobodies-collective/Humans#1000). Cache reads only — no Holded HTTP call is
/// made building it, so the page never inherits the live-contacts latency <c>/Finance/Creditors</c>
/// carries. The mirror's own health (API budget, ledger sweeps, chart of accounts) stays on
/// <c>/Holded</c>; this page links there rather than restating it.
/// </summary>
internal sealed record HoldedConnectorVm(
    HoldedDocSyncVm DocSync,
    int CreditorBindingCount,
    IReadOnlyList<HoldedCategoryMapVm> CategoryMap,
    IReadOnlyList<HoldedDocVm> Docs)
{
    public int ActiveMappings => CategoryMap.Count(m => m.IsActive);
    public int ArchivedMappings => CategoryMap.Count - ActiveMappings;
    public int MatchedDocs => Docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
    public int UnmatchedDocs => Docs.Count - MatchedDocs;
}

/// <summary>
/// Finance's purchase-doc sync singleton, with its age resolved so the page can say "stale"
/// instead of leaving a reader to subtract two timestamps. Every figure on
/// <c>/Finance/HoldedUnmatched</c> and the budget actuals derives from this sync, so a stalled one
/// makes those pages quietly wrong.
/// </summary>
/// <param name="LastSyncAt">Last <b>successful</b> completion, not last attempt: a failed run sets
/// <paramref name="StatusChangedAt"/> and deliberately leaves this pointing at the older success.
/// Labelling it "last run" is what made an erroring connector read as an idle one.</param>
/// <param name="StatusChangedAt">When the connector last entered <paramref name="Status"/> — for an
/// Error row, when the failing attempt happened. Without it a week-old error and a five-minute-old
/// one render identically.</param>
/// <param name="SinceLastSync">Age of <paramref name="LastSyncAt"/>; null when it has never
/// succeeded. Staleness keys on this and not on the attempt, because the question the page answers
/// is how old the cached data is — a run that failed refreshed nothing.</param>
/// <param name="IsStale">True when the sync has not completed inside <see cref="StaleAfter"/> —
/// never having run counts as stale.</param>
internal sealed record HoldedDocSyncVm(
    Instant? LastSyncAt,
    string Status,
    string? LastError,
    int LastSyncedDocCount,
    Duration? SinceLastSync,
    bool IsStale,
    Instant? StatusChangedAt = null)
{
    /// <summary>The nightly job runs at 03:00, so 36 h is one missed run plus half a day of grace.</summary>
    public static readonly Duration StaleAfter = Duration.FromHours(36);
}

/// <summary>One live <c>holded_category_map</c> row — what a budget category is actually booked to
/// today, as opposed to the plan <c>/Finance/HoldedAccounts</c> renders.</summary>
/// <param name="CategoryName">Null when the category is not in the active budget year — a row whose
/// category was deleted or belongs to an earlier year. The provisioning page calls that an Orphan.</param>
internal sealed record HoldedCategoryMapVm(
    Guid BudgetCategoryId,
    string? CategoryName,
    string? GroupName,
    int HoldedAccountNumber,
    string HoldedAccountId,
    string Tag,
    bool IsActive,
    Instant UpdatedAt);

/// <summary>One pulled purchase doc, matched or not. <c>/Finance/HoldedUnmatched</c> shows only the
/// unmatched subset, so this is the only place "why did this doc land on that category" can be
/// answered — hence <paramref name="MatchSource"/> and the raw <paramref name="TagsJson"/>.</summary>
internal sealed record HoldedDocVm(
    string HoldedDocId,
    string DocNumber,
    string ContactName,
    LocalDate Date,
    decimal Total,
    bool? IsApproved,
    HoldedMatchStatus MatchStatus,
    HoldedMatchSource MatchSource,
    string? CategoryName,
    string? BookedAccountId,
    string TagsJson,
    Instant LastSyncedAt);
