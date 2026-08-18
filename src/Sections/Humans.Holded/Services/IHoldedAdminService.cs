using Humans.Base.Interfaces;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

/// <summary>
/// The /Holded screen's read model. Internal: the screen lives in this section, so nothing
/// outside it consumes this — the cross-section surface stays <see cref="Contracts.IHoldedService"/>.
/// </summary>
internal interface IHoldedAdminService : IApplicationService
{
    Task<HoldedAdminOverview> GetOverviewAsync(CancellationToken ct = default);

    /// <summary>One account's ledger page. Null when the number is neither in the chart cache nor
    /// carries a cached line — there is nothing to show and the controller 404s.</summary>
    Task<HoldedAccountStatement?> GetAccountStatementAsync(int number, CancellationToken ct = default);
}
