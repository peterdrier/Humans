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

    // Daybook journal lines (the single source of truth — balance/owed/payments all derive from these).
    /// <summary>Idempotent upsert keyed on (EntryNumber, Line); journal lines are immutable facts.</summary>
    Task UpsertLedgerLinesAsync(IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(int accountNum, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetAllLedgerLinesAsync(CancellationToken ct = default);
    /// <summary>The most recent cached line's date, or null when the cache is empty (drives backfill vs incremental).</summary>
    Task<Instant?> GetLatestLedgerLineDateAsync(CancellationToken ct = default);

    // Creditor contact bindings (member -> Holded creditor account)
    Task<HoldedCreditorContact?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedCreditorContact>> GetCreditorContactsAsync(CancellationToken ct = default);
    Task UpsertCreditorContactAsync(HoldedCreditorContact row, Instant now, CancellationToken ct = default);

    // Sync state (singleton, seeded by migration)
    Task<HoldedSyncState> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
}
