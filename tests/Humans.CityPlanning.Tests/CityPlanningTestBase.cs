using Humans.CityPlanning.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// The three <c>ServiceTestHarness</c> members this project's suites used — an in-memory
/// <see cref="CityPlanningDbContext"/>, a factory over the same store, and a fixed clock.
/// Owned here rather than linked into <c>tests/Directory.Build.props</c>: the harness is
/// built around <c>HumansDbContext</c>, and sharing it would grant a section test project
/// <c>InternalsVisibleTo</c> on Base's context and push Humans.Infrastructure into every
/// test project compiling the shared set (design §15 step 8).
/// Each test gets a fresh instance, so every case gets its own store.
/// </summary>
public abstract class CityPlanningTestBase : IDisposable
{
    private readonly DbContextOptions<CityPlanningDbContext> _dbOptions;

    private protected FakeClock Clock { get; }
    private protected CityPlanningDbContext CityPlanningDb { get; }
    private protected TestDbContextFactory<CityPlanningDbContext> CityPlanningDbFactory { get; }

    private protected CityPlanningTestBase(Instant? now = null)
    {
        _dbOptions = new DbContextOptionsBuilder<CityPlanningDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        CityPlanningDb = new CityPlanningDbContext(_dbOptions);
        CityPlanningDbFactory = new TestDbContextFactory<CityPlanningDbContext>(_dbOptions);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    public void Dispose()
    {
        CityPlanningDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
