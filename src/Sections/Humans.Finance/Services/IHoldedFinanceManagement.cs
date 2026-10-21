using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// Full Finance service surface. Other sections depend on <see cref="IHoldedFinanceService"/>
/// for reads and <see cref="IHoldedFinanceWrite"/> for the few writes they make.
/// </summary>
internal interface IHoldedFinanceManagement : IHoldedFinanceService, IHoldedFinanceWrite, IApplicationService
{
    /// <summary>Pulls every Holded purchase doc into Finance's mirror. Callers: this section's
    /// <c>HoldedDocSyncJob</c> (nightly) and <c>FinanceController.RunHoldedSync</c> (manual).</summary>
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);
}
