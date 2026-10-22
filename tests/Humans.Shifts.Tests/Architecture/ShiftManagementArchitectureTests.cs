using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Humans.Base.Data;
using Npgsql;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NodaTime;
using Humans.Shifts.Contracts;
using ShiftManagementService = Humans.Shifts.Services.ShiftManagementService;

namespace Humans.Shifts.Tests.Architecture;

public class ShiftManagementArchitectureTests
{
    /// <summary>Resolved from the production <c>Section.Register</c>, not a test-built mirror of it.</summary>
    [HumansFact]
    public void IShiftManagement_And_IShiftManagementService_ResolveToSameInstance()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var fromManagement = scope.ServiceProvider.GetRequiredService<IShiftManagement>();
        var fromPublic = scope.ServiceProvider.GetRequiredService<IShiftService>();

        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must forward to the one ShiftManagementService per scope");
    }

    /// <summary>Resolved from the production <c>Section.Register</c>, not a test-built mirror of it.</summary>
    [HumansFact]
    public void IVolunteerTrackingManagement_And_IVolunteerTrackingService_ResolveToSameInstance()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var fromManagement = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingManagement>();
        var fromPublic = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingService>();

        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must forward to the one VolunteerTrackingService per scope");
    }

    private static ServiceProvider BuildProvider()
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
        // Only constructed, never connected: the repositories build their contexts lazily.
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<QueryStatistics>();
        services.AddSingleton<QueryMonitoringInterceptor>();
        services.AddSingleton(new NpgsqlDataSourceBuilder("Host=localhost").Build());
        new Section().Register(services, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
