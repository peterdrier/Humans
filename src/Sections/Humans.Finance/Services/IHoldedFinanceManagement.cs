using Humans.Base.Interfaces;
using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

internal interface IHoldedFinanceManagement : IHoldedFinanceService, IHoldedFinanceWrite, IApplicationService
{
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);
}
