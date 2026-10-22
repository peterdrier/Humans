using AwesomeAssertions;
using Humans.Base.Interfaces.Caching;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Teams.Tests.Services;

/// <summary>
/// The real-graph half of <c>DependencyCycleResolutionTests</c>, here because it needs the concrete
/// <c>TeamService</c>, internal to this assembly. <c>TeamService</c> resolves <c>ISystemTeamSync</c>
/// and the Google sync surface lazily through <c>IServiceProvider</c> to break the cycle back.
/// </summary>
public sealed class TeamsDependencyCycleTests
{
    [HumansFact]
    public void TeamService_Resolves_WhenTheRealTeamsChainIsRegistered()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<IEmailService>(_ => Substitute.For<IEmailService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<ITeamRepository>(_ => Substitute.For<ITeamRepository>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());

        // Users, Auth and Shifts are other sections whose own tests pin their graphs; the
        // subject here is the Teams chain.
        services.AddScoped<IUserService>(_ => Substitute.For<IUserService>());

        services.AddScoped<IRoleAssignmentService>(_ => Substitute.For<IRoleAssignmentService>());

        services.AddScoped<IShiftManagement>(_ => Substitute.For<IShiftManagement>());
        services.AddScoped<IShiftService>(_ => Substitute.For<IShiftService>());

        services.AddScoped<TeamService>();
        services.AddScoped<ITeamManagement>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<TeamService>>(_ => NullLogger<TeamService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<ITeamManagement>();

        resolve.Should().NotThrow();
        resolve().Should().BeOfType<TeamService>();
    }
}
