using Humans.Shifts.Services;
using Humans.Auth.Contracts;
using Humans.Application.Services.Profiles;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Auth;
using Humans.Application.Services.Users;
using Humans.Email.Contracts;
using Humans.Infrastructure.Data;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Teams.Tests;

/// <summary>
/// The real-graph half of <c>DependencyCycleResolutionTests</c>: <c>UserService</c> injects
/// <see cref="ITeamService"/> and <c>TeamService</c> resolves <c>ISystemTeamSync</c> and the
/// Google sync surface lazily through <c>IServiceProvider</c> to break the cycle back. The
/// assertion needs the concrete <c>TeamService</c> in the graph, which is internal to this
/// assembly now, so the method lives here rather than in Humans.Application.Tests
/// (design §15 step 8; CityPlanning finding 18's shape, resolved by moving).
/// </summary>
public sealed class TeamsDependencyCycleTests
{
    [HumansFact]
    public void IUserService_Resolves_WhenTeamServiceAndRoleAssignmentServiceAreRegistered()
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new UsersDbContext(
            new DbContextOptionsBuilder<UsersDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));
        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IUserRepository>(_ => Substitute.For<IUserRepository>());
        services.AddScoped<IUserRepository>(_ => Substitute.For<IUserRepository>());
        services.AddScoped<ICommunicationPreferenceRepository>(_ => Substitute.For<ICommunicationPreferenceRepository>());
        services.AddScoped<IUserInfoInvalidator>(_ => Substitute.For<IUserInfoInvalidator>());
        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<IEmailService>(_ => Substitute.For<IEmailService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<INavBadgeCacheInvalidator>(_ => Substitute.For<INavBadgeCacheInvalidator>());
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator>(_ => Substitute.For<IRoleAssignmentClaimsCacheInvalidator>());
        services.AddScoped<ITeamRepository>(_ => Substitute.For<ITeamRepository>());
        services.AddScoped<INotificationMeterCacheInvalidator>(_ => Substitute.For<INotificationMeterCacheInvalidator>());
        services.AddScoped<IShiftAuthorizationInvalidator>(_ => Substitute.For<IShiftAuthorizationInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());

        services.AddScoped<UserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UserService>());

        // Auth is another section; RoleAssignmentService is internal to Humans.Auth and its
        // own constructor shape is pinned by that section's AuthArchitectureTests. The
        // subject here is the Teams chain.
        services.AddScoped<IRoleAssignmentService>(_ => Substitute.For<IRoleAssignmentService>());

        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserService>>(_ => NullLogger<UserService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<ShiftManagementService>>(_ => NullLogger<ShiftManagementService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<TeamService>>(_ => NullLogger<TeamService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserEmailService>>(_ => NullLogger<UserEmailService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<IUserService>();

        resolve.Should().NotThrow();
        resolve().Should().BeOfType<UserService>();
    }
}
