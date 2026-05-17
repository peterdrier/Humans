using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Admin;

internal sealed class AdminDatabaseDiagnosticsRepository : IAdminDatabaseDiagnosticsRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public AdminDatabaseDiagnosticsRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = await db.Database.GetPendingMigrationsAsync(ct);

        return new DatabaseMigrationStatus(
            LastApplied: applied.LastOrDefault(),
            AppliedCount: applied.Count,
            PendingCount: pending.Count());
    }

    public async Task<int> ClearHangfireLocksAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock", ct);
    }
}
