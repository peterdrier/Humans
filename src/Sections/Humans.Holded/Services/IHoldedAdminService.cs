using Humans.Base.Interfaces;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

internal interface IHoldedAdminService : IApplicationService
{
    Task<HoldedAdminOverview> GetOverviewAsync(CancellationToken ct = default);

    /// <summary>Null when the number is neither in the chart cache nor on a cached line.</summary>
    Task<HoldedAccountStatement?> GetAccountStatementAsync(int number, CancellationToken ct = default);

    /// <summary>Null when no cached line carries this entry number.</summary>
    Task<HoldedEntry?> GetEntryAsync(int entryNumber, CancellationToken ct = default);
}
