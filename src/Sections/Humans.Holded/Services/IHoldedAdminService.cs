using Humans.Application.Interfaces;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

/// <summary>
/// The /Holded screen's read model. Internal: the screen lives in this section, so nothing
/// outside it consumes this — the cross-section surface stays <see cref="Contracts.IHoldedService"/>.
/// </summary>
internal interface IHoldedAdminService : IApplicationService
{
    Task<HoldedAdminOverview> GetOverviewAsync(CancellationToken ct = default);
}
