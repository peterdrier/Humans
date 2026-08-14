using Humans.Shifts.Services;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Auth;
using Humans.Users.Services;
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
    public void TeamService_Resolves_WhenTheRealTeamsChainIsRegistered()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

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
        services.AddScoped<IEarlyEntryInvalidator>(_ => Substitute.For<IEarlyEntryInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());

        // Users is another section; UserService, its DbContext and its two repository
        // interfaces are internal to Humans.Users and its own graph is pinned by that
        // section's own tests. Same call as IRoleAssignmentService below — the subject
        // here is the Teams chain (#866, G5 lane 2).
        services.AddScoped<IUserService>(_ => Substitute.For<IUserService>());

        // Auth is another section; RoleAssignmentService is internal to Humans.Auth and its
        // own constructor shape is pinned by that section's AuthArchitectureTests. The
        // subject here is the Teams chain.
        services.AddScoped<IRoleAssignmentService>(_ => Substitute.For<IRoleAssignmentService>());

        // Shifts is another section; its concrete service and repository are internal to
        // Humans.Shifts and its own graph is pinned by that section's own tests.
        services.AddScoped<IShiftManagementService>(_ => Substitute.For<IShiftManagementService>());
        services.AddScoped<IShiftManagementServiceRead>(_ => Substitute.For<IShiftManagementServiceRead>());

        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<TeamService>>(_ => NullLogger<TeamService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<ITeamService>();

        resolve.Should().NotThrow();
        resolve().Should().BeOfType<TeamService>();
    }
}
