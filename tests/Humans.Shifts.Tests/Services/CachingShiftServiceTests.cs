using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Data;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using Npgsql;
using NSubstitute;

namespace Humans.Shifts.Tests.Services;

/// <summary>Through the production <c>Section.Register</c>: a write on any Shifts interface shows up in the cached rows.</summary>
public class CachingShiftServiceTests
{
    [HumansFact]
    public async Task Writes_Through_Any_Interface_Refresh_The_Cached_Rotas()
    {
        var options = new DbContextOptionsBuilder<ShiftsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var now = Instant.FromUtc(2026, 3, 1, 12, 0);
        var rota = new Rota { Id = Guid.NewGuid(), EventSettingsId = Guid.NewGuid(), Name = "Gate", CreatedAt = now, UpdatedAt = now };
        var kept = new Shift { Id = Guid.NewGuid(), RotaId = rota.Id, Duration = Duration.FromHours(4) };
        var deleted = new Shift { Id = Guid.NewGuid(), RotaId = rota.Id, Duration = Duration.FromHours(4) };
        await using (var seed = new ShiftsDbContext(options))
        {
            seed.AddRange(rota, kept, deleted);
            await seed.SaveChangesAsync();
        }

        await using var provider = BuildProvider(options);
        var rows = provider.GetRequiredService<IShiftRotaRows>();
        (await rows.GetRotaRowsAsync()).Single().Shifts.Should().HaveCount(2);

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IShiftManagement>().DeleteShiftAsync(deleted.Id, Guid.NewGuid());
        (await rows.GetRotaRowsAsync()).Single().Shifts.Select(s => s.Shift.Id).Should().Equal(kept.Id);

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IShiftManagement>().DeleteRotaAsync(rota.Id, Guid.NewGuid());
        (await rows.GetRotaRowsAsync()).Should().BeEmpty();
    }

    private static ServiceProvider BuildProvider(DbContextOptions<ShiftsDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton(Substitute.For<IAuditLogService>());
        services.AddSingleton(Substitute.For<IAdminAuthorizationService>());
        services.AddSingleton(Substitute.For<IUserService>());
        services.AddSingleton(Substitute.For<ITicketService>());
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<QueryStatistics>();
        services.AddSingleton<QueryMonitoringInterceptor>();
        services.AddSingleton(new NpgsqlDataSourceBuilder("Host=localhost").Build());
        new Section().Register(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IDbContextFactory<ShiftsDbContext>>(new TestDbContextFactory<ShiftsDbContext>(options));
        services.AddScoped(_ => new ShiftsDbContext(options));
        return services.BuildServiceProvider();
    }
}
