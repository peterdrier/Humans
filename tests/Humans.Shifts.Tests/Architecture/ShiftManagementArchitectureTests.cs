using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Humans.Base.Data;
using Npgsql;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NodaTime;
using Humans.Shifts.Contracts;
using ShiftManagementService = Humans.Shifts.Services.ShiftManagementService;

namespace Humans.Shifts.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the
/// <c>ShiftManagementService</c> portion of the Shifts section (issue nobodies-collective/Humans#541a).
/// Sibling services (<c>ShiftSignupService</c>, <c>VolunteerTrackingService</c>)
/// cover signup and user-oriented tracking workflows.
/// </summary>
public class ShiftManagementArchitectureTests
{
    [HumansFact]
    public void ShiftManagementService_ImplementsShiftAuthorizationInvalidator()
    {
        typeof(IShiftAuthorizationInvalidator).IsAssignableFrom(typeof(ShiftManagementService))
            .Should().BeTrue(
                because: "the service owns the shift-auth cache and external sections (Profile deletion) drop it through this invalidator rather than poking IMemoryCache directly");
    }

    /// <summary>
    /// Asserts that <see cref="IShiftManagement"/> and <see cref="IShiftManagementService"/> resolve to the same
    /// scoped instance from the production DI registration — <c>Section.Register</c>
    /// itself, not a test-built mirror of it.
    /// </summary>
    [HumansFact]
    public void IShiftManagement_And_IShiftManagementService_ResolveToSameInstance()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var fromManagement = scope.ServiceProvider.GetRequiredService<IShiftManagement>();
        var fromPublic = scope.ServiceProvider.GetRequiredService<IShiftManagementService>();

        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must forward to the one ShiftManagementService per scope");
    }

    /// <summary>
    /// Asserts that <see cref="IVolunteerTrackingManagement"/> and <see cref="IVolunteerTrackingService"/> resolve to the same
    /// scoped instance from the production DI registration — <c>Section.Register</c>
    /// itself, not a test-built mirror of it.
    /// </summary>
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

    /// <summary>
    /// The section's production registrations, plus stand-ins for the other
    /// sections' contracts the Shifts services are constructed with.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton(Substitute.For<IAuditLogService>());
        services.AddSingleton(Substitute.For<IAdminAuthorizationService>());
        services.AddSingleton(Substitute.For<IEarlyEntryInvalidator>());
        services.AddSingleton(Substitute.For<IUserService>());
        // Only constructed, never connected: the repositories build their contexts lazily.
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<QueryStatistics>();
        services.AddSingleton<QueryMonitoringInterceptor>();
        services.AddSingleton(new NpgsqlDataSourceBuilder("Host=localhost").Build());
        new Section().Register(services, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
