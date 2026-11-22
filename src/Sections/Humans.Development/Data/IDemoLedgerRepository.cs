using Humans.Base.Interfaces.Repositories;
using Humans.Development.Domain;

namespace Humans.Development.Data;

internal interface IDemoLedgerRepository : IRepository
{
    /// <summary>Every entry, all generations: the ledger is small enough to hold whole.</summary>
    Task<IReadOnlyList<DemoLedgerEntry>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts the entry or replaces the one with its id.</summary>
    Task SaveAsync(DemoLedgerEntry entry, CancellationToken ct = default);
}
