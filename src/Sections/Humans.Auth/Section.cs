using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Services;
using Humans.Auth.ViewComponents;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Auth;

public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionDataMigrations,
    ISectionRoles,
    IUserPart
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, AuthNotificationSources>();
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");

        // Composed from every section's ISectionRoles (registered by discovery); throws on a duplicate name.
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));

        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // IUserDataContributor and IUserMerge reach the undecorated inner through the unkeyed concrete, which
        // forwards to this keyed registration, so there is only one inner instance.
        services.AddKeyedScoped<IRoleAssignmentManagement, RoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey);
        services.AddScoped<RoleAssignmentService>(sp =>
            (RoleAssignmentService)sp.GetRequiredKeyedService<IRoleAssignmentManagement>(
                CachingRoleAssignmentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddSingleton<CachingRoleAssignmentService>();
        services.AddSingleton<IRoleAssignmentManagement>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<IRoleAssignmentService>(sp =>
            sp.GetRequiredService<IRoleAssignmentManagement>());
        services.AddSingleton<IRoleAssignmentWrite>(sp =>
            sp.GetRequiredService<IRoleAssignmentManagement>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleAssignmentService>());

        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();

        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        services.AddScoped<AuthEmails>();
        services.AddScoped<IEmailPreviewContributor, AuthEmailPreviews>();

        services.AddSingleton<AuthMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthMetricsService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Users", [
            new("Roles", "RoleAssignments", "Roles", null, null, "fa-solid fa-id-badge", UsersPolicies.Read, Weight: 10)
        ])
    ];

    internal const string RoleRenamesKey = "auth.role-renames";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(RoleRenamesKey, "Auth",
            "Rewrite role assignments stored under a former role name (HumanAdmin, CampAdmin, TicketAdmin, NoInfoAdmin, VolunteerCoordinator, EETeamAdmin) to the current name",
            "RoleDefinition.FormerNames on each renamed role and the former-name reads in RoleAssignmentService",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Stored role", "Current role", "Valid to"],
            [.. (await sp.GetRequiredService<RoleAssignmentService>().GetRowsUnderFormerNamesAsync(ct))
                .Select(r => (IReadOnlyList<string>)
                [
                    r.UserId.ToString(), r.StoredName, r.CurrentName, r.ValidTo.ToIso8601() ?? "open",
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<RoleAssignmentService>().RewriteFormerNamesAsync(ct);

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(RoleNames.Board, "Board",
            "Elected stewards of the collective. They guide strategy, approve tier applications, and make the big decisions so the rest of us can focus on making things happen.",
            "fa-solid fa-crown"),
        new(RoleNames.Admin, "Admin",
            "The ones who keep the lights on. Full system access, all the keys, all the responsibility. When something breaks at 3am, these are the humans who fix it.",
            "fa-solid fa-gear"),
    ];

    IEnumerable<UserPart> IUserPart.Parts() =>
        [new(UserPartSlots.AdminDetail, typeof(RoleAssignmentsCardViewComponent), Weight: 50)];
}
