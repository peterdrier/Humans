using Humans.Base.Interfaces;
using Humans.Holded.Models;

namespace Humans.Holded.Services;

internal interface IHoldedAdminService : IApplicationService
{
    /// <summary>The mirror's state plus one live call (GET /usage), whose failure leaves the rest intact.</summary>
    Task<HoldedSummary> GetSummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedMonthlyCalls>> GetCallsByMonthAsync(CancellationToken ct = default);

    Task<HoldedAccountsPage> GetAccountsPageAsync(HoldedAccountsFilter filter, CancellationToken ct = default);

    /// <summary>Null when the number is neither in the chart cache nor on a cached line.</summary>
    Task<HoldedAccountStatement?> GetAccountStatementAsync(int number, CancellationToken ct = default);

    /// <summary>Null when no cached line carries this entry number.</summary>
    Task<HoldedEntry?> GetEntryViewAsync(int entryNumber, CancellationToken ct = default);
}
