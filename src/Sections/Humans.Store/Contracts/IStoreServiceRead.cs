using Humans.Base.Interfaces;

namespace Humans.Store.Contracts;

/// <summary>
/// Cross-section read surface for Store. One consumer today: the admin dashboard
/// tile reads the active year's order/revenue summary.
/// </summary>
public interface IStoreServiceRead : IApplicationService
{
    Task<SummaryDto> GetStoreSummaryAsync(int year, CancellationToken ct = default);
}
