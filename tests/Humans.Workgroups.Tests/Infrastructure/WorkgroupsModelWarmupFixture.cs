using Humans.Workgroups.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Humans.Workgroups.Tests.Infrastructure;

/// <summary>
/// Forces EF's one-time <see cref="WorkgroupsDbContext"/> model build before any test in the
/// assembly runs (see AssemblyFixtures.cs), so the cost lands here instead of inside whichever
/// <c>[HumansFact]</c>/<c>[HumansTheory]</c> happens to run first on a cold, contended runner —
/// src/Sections/Humans.Workgroups/Docs/debt.yml WG-1.
/// </summary>
public sealed class WorkgroupsModelWarmupFixture : IDisposable
{
    private readonly WorkgroupsDbContext _context = new(
        new DbContextOptionsBuilder<WorkgroupsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public WorkgroupsModelWarmupFixture() => _context.Database.EnsureCreated();

    public void Dispose() => _context.Dispose();
}
