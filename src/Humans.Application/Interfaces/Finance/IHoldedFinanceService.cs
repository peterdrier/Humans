using Humans.Application.Interfaces;
using Humans.Application.Services.Finance.Dtos;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedFinanceService : IApplicationService
{
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);

    /// <summary>Nightly cache refresh: creditor balances (chartofaccounts) + payments rows.</summary>
    Task SyncCreditorDataAsync(CancellationToken ct = default);

    /// <summary>Reads cached creditor status for a member by their supplier-account number + contact id.
    /// Returns null when nothing is cached yet (not registered in Holded).</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, string holdedContactId, CancellationToken ct = default);
}
