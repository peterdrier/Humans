using Humans.Base.Interfaces.Repositories;
using Humans.Finance.Domain;
using NodaTime;

namespace Humans.Finance.Data;

internal interface IHoldedRepository : IRepository
{
    // Category map
    Task<IReadOnlyList<HoldedCategoryMap>> GetCategoryMapAsync(CancellationToken ct = default);
    Task AddCategoryMapAsync(HoldedCategoryMap row, CancellationToken ct = default);

    // Docs
    Task UpsertDocsAsync(IReadOnlyList<HoldedExpenseDoc> docs, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetUnmatchedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedExpenseDoc>> GetMatchedForYearAsync(int calendarYear, CancellationToken ct = default);

    /// <summary>Every pulled doc, matched or not, for the <c>/Finance/Holded</c> browse panel. The
    /// two reads above are each a filtered slice — unmatched-only, and matched-in-one-year — so
    /// neither composes into "all docs". Unordered: the caller sorts for display.</summary>
    Task<IReadOnlyList<HoldedExpenseDoc>> GetAllDocsAsync(CancellationToken ct = default);

    // Creditor contact bindings (member -> Holded creditor account)
    Task<HoldedCreditorContact?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedCreditorContact>> GetCreditorContactsAsync(CancellationToken ct = default);
    Task UpsertCreditorContactAsync(HoldedCreditorContact row, Instant now, CancellationToken ct = default);

    /// <summary>Removes the member's binding row. Returns false when there was none.</summary>
    Task<bool> DeleteCreditorContactAsync(Guid userId, CancellationToken ct = default);

    // Purchase-doc sync state (singleton, lazy-created)
    Task<HoldedDocSyncState> GetOrCreateDocSyncStateAsync(CancellationToken ct = default);
    Task SaveDocSyncStateAsync(HoldedDocSyncState state, CancellationToken ct = default);
}
