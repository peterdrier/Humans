using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Infrastructure.Repositories.Admin;

internal sealed class AdminDatabaseDiagnosticsRepository(
    IDbContextFactory<SystemDbContext> factory,
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
        // Every one of them is listed, Users included: UsersDbContext held the top-level
        // "main pile" slot only because HumansDbContext's deletion (#858) left it there, and
        // it stopped being reachable from Base at all when the section moved into
        // Humans.Users (#866, G5 lane 2). The top-level fields now report SystemDbContext,
        // which is Base's own context and the one this repository already opens for the
        // Hangfire lock statement.
        var sections = new List<SectionMigrationStatus>();
        using var scope = scopeFactory.CreateScope();
        foreach (var section in sectionContexts)
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
