using Humans.Base.Interfaces;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

internal interface IHoldedAdminService : IApplicationService
{
    /// <summary>The mirror's state and the stored usage reading; no Holded call.</summary>
    Task<HoldedSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>One GET /usage into the stored reading. False when Holded failed; the failure is on the reading.</summary>
    Task<bool> RefreshUsageAsync(CancellationToken ct = default);

    /// <summary>Purchase documents, contacts, then the trailing ledger sweep, each isolated so one failure does not
    /// skip the others. Caller cancellation still propagates.</summary>
    Task<HoldedSyncNowResult> SyncNowAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedMonthlyCalls>> GetCallsByMonthAsync(CancellationToken ct = default);

    Task<HoldedAccountsPage> GetAccountsPageAsync(HoldedAccountsFilter filter, CancellationToken ct = default);

    /// <summary>Null when the number is neither in the chart cache nor on a cached line.</summary>
    Task<HoldedAccountStatement?> GetAccountStatementAsync(int number, CancellationToken ct = default);

    /// <summary>Null when no cached line carries this entry number.</summary>
    Task<HoldedEntry?> GetEntryViewAsync(int entryNumber, CancellationToken ct = default);
}
