using Humans.Base.Interfaces.Repositories;
using Humans.Pleo.Domain;
using NodaTime;

namespace Humans.Pleo.Data;

internal interface IPleoRepository : IRepository
{
    /// <summary>The sweep is the truth: entries, employees and accounts it did not return are deleted, and the balance
    /// row is replaced. One save, so a page never sees half a sweep. <c>pleo_categories</c> is not touched.</summary>
    Task ReplaceMirrorAsync(PleoMirrorSnapshot snapshot, Instant now, CancellationToken ct = default);

    Task<IReadOnlyList<PleoEntry>> GetEntriesAsync(CancellationToken ct = default);
    Task<PleoEntry?> GetEntryAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PleoEmployee>> GetEmployeesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PleoAccount>> GetAccountsAsync(CancellationToken ct = default);
    Task<PleoBalance?> GetBalanceAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PleoCategory>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates by <see cref="PleoCategory.Id"/>.</summary>
    Task SaveCategoryAsync(PleoCategory category, CancellationToken ct = default);

    Task<PleoSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(PleoSyncState state, CancellationToken ct = default);
}

internal sealed record PleoMirrorSnapshot(
    IReadOnlyList<PleoEntry> Entries,
    IReadOnlyList<PleoEmployee> Employees,
    IReadOnlyList<PleoAccount> Accounts,
    PleoBalance Balance);
