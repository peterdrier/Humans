using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Infrastructure.Repositories.Admin;

internal sealed class AdminDatabaseDiagnosticsRepository(
    IDbContextFactory<UsersDbContext> factory,
    IServiceScopeFactory scopeFactory,
    IEnumerable<SectionDbContextRegistration> sectionContexts)
    : IAdminDatabaseDiagnosticsRepository
{
    public async Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = await db.Database.GetPendingMigrationsAsync(ct);

        // Section contexts are scoped (registered via AddSectionDbContext), so
        // resolve them through a scope — same pattern as DatabaseMigrationHostedService.
        // UsersDbContext is excluded: the top-level fields above already report it
        // (it took over the old main-pile slot), so listing it again would
        // double-report Users to the deployment tooling polling this endpoint.
        var sections = new List<SectionMigrationStatus>();
        using var scope = scopeFactory.CreateScope();
        foreach (var section in sectionContexts.Where(s => s.ContextType != typeof(UsersDbContext)))
        {
            var sectionDb = (DbContext)scope.ServiceProvider.GetRequiredService(section.ContextType);
            var sectionApplied = (await sectionDb.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var sectionPending = await sectionDb.Database.GetPendingMigrationsAsync(ct);
            sections.Add(new SectionMigrationStatus(
                Context: section.ContextType.Name,
                LastApplied: sectionApplied.LastOrDefault(),
                AppliedCount: sectionApplied.Count,
                PendingCount: sectionPending.Count()));
        }

        return new DatabaseMigrationStatus(
            LastApplied: applied.LastOrDefault(),
            AppliedCount: applied.Count,
            PendingCount: pending.Count(),
            Applied: applied,
            Sections: sections);
    }

    public async Task<int> ClearHangfireLocksAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock", ct);
    }
}
