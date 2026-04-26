using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// EF-backed access to Finance section tables (holded_transactions,
/// holded_sync_states). Singleton + IDbContextFactory per design-rules §15b.
/// </summary>
public interface IHoldedRepository
{
    // Sync state
    Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task SetSyncStateAsync(HoldedSyncStatus status, Instant changedAt, string? lastError, CancellationToken ct = default);
    Task RecordSyncCompletedAsync(Instant completedAt, int docCount, CancellationToken ct = default);

    // Upsert + reads
    Task UpsertAsync(HoldedTransaction transaction, CancellationToken ct = default);
    Task UpsertManyAsync(IReadOnlyList<HoldedTransaction> transactions, CancellationToken ct = default);
    Task<HoldedTransaction?> GetByHoldedDocIdAsync(string holdedDocId, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedTransaction>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedTransaction>> GetByCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, decimal>> GetActualSumsByCategoryAsync(Guid budgetYearId, CancellationToken ct = default);
    Task<int> CountUnmatchedAsync(CancellationToken ct = default);

    // Manual reassignment (writes BudgetCategoryId + MatchStatus)
    Task AssignCategoryAsync(string holdedDocId, Guid budgetCategoryId, Instant updatedAt, CancellationToken ct = default);
}
