using Humans.CityPlanning.Authorization;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using Microsoft.Extensions.DependencyInjection;
using Humans.Settings.Contracts;
using NSubstitute;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// An in-memory <see cref="CityPlanningDbContext"/>, a factory over the same store, and a fixed
/// clock, owned here: sharing the <c>UsersDbContext</c>-based harness would grant this project
/// <c>InternalsVisibleTo</c> on another section's context. Each test gets its own store.
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

    /// <summary>
    /// An <see cref="ISettingsService"/> whose active event is <paramref name="year"/>'s —
    /// city planning's current year. Null means no active event.
    /// </summary>
    private protected static ISettingsService ActiveEvent(int? year = 2026)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(year is { } y ? BurnFixtures.Burn(year: y) : null);
        return settings;
    }

    /// <summary>
    /// The section's real <c>CityPlanningMapAdmin</c> policy over <paramref name="cityPlanning"/>,
    /// so a controller test runs the same gate production does.
    /// </summary>
    private protected static IAuthorizationService MapAdminAuthorization(ICityPlanningService cityPlanning)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(new SectionPolicies().AddPolicies);
        services.AddSingleton<IAuthorizationHandler>(new CityPlanningMapAdminHandler(cityPlanning));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    public void Dispose()
    {
        CityPlanningDb.Dispose();
        GC.SuppressFinalize(this);
    }
}
