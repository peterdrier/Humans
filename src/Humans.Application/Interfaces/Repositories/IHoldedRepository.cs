using Humans.Domain.Attributes;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

[Section("Finance")]
public interface IHoldedRepository : IRepository
{
    // Category map
    Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default);
    Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default);

    // Docs
    Task UpsertDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default);

    // Sync state (singleton, seeded by migration)
    Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
}
