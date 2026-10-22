using Humans.Finance.Contracts;
using Humans.Finance.Domain;
using NodaTime;

namespace Humans.Finance.Models;

/// <summary>
/// Cache reads only, so the page never inherits <c>/Finance/Creditors</c>' live-contacts latency. The mirror's own
/// health stays on <c>/Holded</c>.
/// </summary>
internal sealed record HoldedConnectorVm(
    HoldedDocSyncVm DocSync,
    int CreditorBindingCount,
    IReadOnlyList<HoldedCategoryMapRow> CategoryMap,
    IReadOnlyList<HoldedManagedAccountVm> ManagedAccounts,
    IReadOnlyList<HoldedDocVm> Docs)
{
    public int ActiveMappings => CategoryMap.Count(m => m.IsActive);
    public int ArchivedMappings => CategoryMap.Count - ActiveMappings;
    public int MatchedDocs => Docs.Count(d => d.MatchStatus == HoldedMatchStatus.Matched);
    public int UnmatchedDocs => Docs.Count - MatchedDocs;
}

/// <summary>
/// The unmatched queue and the budget actuals all derive from this sync, so a stalled one makes them quietly wrong.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="LastSyncAt"/> is the last <b>successful</b> run: a failed run sets
/// <paramref name="StatusChangedAt"/> and leaves this on the older success. Calling it "last run" would make an
/// erroring connector look idle.</item>
/// <item><paramref name="StatusChangedAt"/> tells a week-old error from a five-minute-old one.</item>
/// <item><paramref name="SinceLastSync"/> is null when it never succeeded. Staleness keys on it, not on the attempt:
/// a failed run refreshed nothing.</item>
/// <item><paramref name="IsStale"/>: no completion inside <see cref="StaleAfter"/>; never having run counts.</item>
/// </list>
/// </remarks>
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

/// <summary>Created outside the budget map.</summary>
internal sealed record HoldedManagedAccountVm(int AccountNumber, string AccountId, string Label, bool IsActive, Instant CreatedAt);

/// <summary>Matched or not: the only place "why did this doc land on that category" can be answered, hence
/// <paramref name="MatchSource"/> and the raw <paramref name="TagsJson"/>.</summary>
internal sealed record HoldedDocVm(
    string HoldedDocId,
    string DocNumber,
    string ContactName,
    string? Description,
    string HoldedUrl,
    LocalDate Date,
    decimal Total,
    bool? IsApproved,
    HoldedMatchStatus MatchStatus,
    HoldedMatchSource MatchSource,
    string? CategoryName,
    string? BookedAccountId,
    string TagsJson,
    Instant LastSyncedAt);

internal sealed record HoldedProvisioningRow(
    Guid BudgetCategoryId, string CategoryName, string GroupName,
    int? ExistingAccountNum, int? ProposedAccountNum, string Tag, string State); // Mapped|ToAdd|Orphan

internal sealed record HoldedProvisioningPlan(
    IReadOnlyList<HoldedProvisioningRow> Rows, int NextNumber);
