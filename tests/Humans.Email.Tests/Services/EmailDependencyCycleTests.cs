using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Auth;
using Humans.Application.Services.Profiles;
using Humans.Application.Services.Shifts;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;
using Humans.Notifications.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Services;

/// <summary>
/// The real send chain — <c>UserService</c> → <c>UserEmailService</c> →
/// <c>OutboxEmailService</c> → back to <c>IUserEmailService</c> — resolves without a
/// constructor cycle.
/// </summary>
/// <remarks>
/// Lived in <c>Humans.Application.Tests</c>' <c>DependencyCycleResolutionTests</c> until
/// the section's G5 move made <c>OutboxEmailService</c> internal to another assembly
/// (CityPlanning finding 18's shape). It belongs here now: the section owns the concrete
/// type, and the Base half of the same guard — <c>IUserService</c> resolving with a
/// substituted <c>IEmailService</c> — stayed where it was.
/// </remarks>
public sealed class EmailDependencyCycleTests
{
    [HumansFact]
    public void IUserService_And_IEmailService_Resolve_WhenRealEmailChainIsRegistered()
    {
        var services = new ServiceCollection();
        var userStore = Substitute.For<IUserStore<User>>();

        services.AddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddScoped<IUserRepository>(_ => Substitute.For<IUserRepository>());
        services.AddScoped<ICommunicationPreferenceRepository>(_ => Substitute.For<ICommunicationPreferenceRepository>());
        services.AddScoped<IUserInfoInvalidator>(_ => Substitute.For<IUserInfoInvalidator>());
        services.AddScoped<IShiftManagementRepository>(_ => Substitute.For<IShiftManagementRepository>());
        services.AddScoped<IAuditLogService>(_ => Substitute.For<IAuditLogService>());
        services.AddScoped<INotificationEmitter>(_ => Substitute.For<INotificationEmitter>());
        services.AddScoped<ISystemTeamSync>(_ => Substitute.For<ISystemTeamSync>());
        services.AddScoped<INavBadgeCacheInvalidator>(_ => Substitute.For<INavBadgeCacheInvalidator>());
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator>(_ => Substitute.For<IRoleAssignmentClaimsCacheInvalidator>());
        services.AddScoped<INotificationMeterCacheInvalidator>(_ => Substitute.For<INotificationMeterCacheInvalidator>());
        services.AddScoped<IShiftAuthorizationInvalidator>(_ => Substitute.For<IShiftAuthorizationInvalidator>());
        services.AddScoped<IAdminAuthorizationService>(_ => Substitute.For<IAdminAuthorizationService>());
        services.AddScoped<IEmailOutboxRepository>(_ => Substitute.For<IEmailOutboxRepository>());
        services.AddScoped<IEmailBodyComposer>(_ => Substitute.For<IEmailBodyComposer>());
        services.AddScoped<IImmediateOutboxProcessor>(_ => Substitute.For<IImmediateOutboxProcessor>());
        services.AddScoped<IHumansMetrics>(_ => Substitute.For<IHumansMetrics>());
        services.AddScoped<ICommunicationPreferenceService>(_ => Substitute.For<ICommunicationPreferenceService>());
        services.AddScoped<NodaTime.IClock>(_ => Substitute.For<NodaTime.IClock>());
        services.AddScoped<UserManager<User>>(_ =>
            Substitute.For<UserManager<User>>(userStore, null, null, null, null, null, null, null, null));

        services.AddScoped<UserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UserService>());

        // Auth is another section; RoleAssignmentService is internal to Humans.Auth and its
        // own constructor shape is pinned by AuthArchitectureTests. Same call as ITeamService
        // below — the subject here is the email chain.
        services.AddScoped<IRoleAssignmentService>(_ => Substitute.For<IRoleAssignmentService>());

        services.AddScoped<ShiftManagementService>();
        services.AddScoped<IShiftManagementService>(sp => sp.GetRequiredService<ShiftManagementService>());

        services.AddScoped<UserEmailService>();
        services.AddScoped<IUserEmailService>(sp => sp.GetRequiredService<UserEmailService>());

        services.AddScoped<OutboxEmailService>();
        services.AddScoped<IEmailService>(sp => sp.GetRequiredService<OutboxEmailService>());

        // Teams is another section; its concrete service is internal to Humans.Teams and its
        // own cycle is pinned by TeamsDependencyCycleTests. The subject here is the email chain.
        services.AddScoped<ITeamService>(_ => Substitute.For<ITeamService>());

        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserService>>(_ => NullLogger<UserService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<ShiftManagementService>>(_ => NullLogger<ShiftManagementService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<OutboxEmailService>>(_ => NullLogger<OutboxEmailService>.Instance);
        services.AddScoped<Microsoft.Extensions.Logging.ILogger<UserEmailService>>(_ => NullLogger<UserEmailService>.Instance);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var resolveUserService = () => scope.ServiceProvider.GetRequiredService<IUserService>();
        var resolveEmailService = () => scope.ServiceProvider.GetRequiredService<IEmailService>();

        resolveUserService.Should().NotThrow();
        resolveEmailService.Should().NotThrow();
        resolveUserService().Should().BeOfType<UserService>();
        resolveEmailService().Should().BeOfType<OutboxEmailService>();
    }
}
