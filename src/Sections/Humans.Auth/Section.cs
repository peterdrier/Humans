using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Services;
using Humans.Auth.ViewComponents;
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
using Humans.AuditLog.Contracts;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Auth;

public sealed class Section :
    ISection,
    ISectionAccessMatrix,
    ISectionHelp,
    ISectionAdminNav,
    ISectionDataMigrations,
    ISectionRoles,
    IUserPart
{
    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "SystemRoles",
            SectionName = "System Roles",
            Order = 91,
            Roles = ["Board", "UsersAdmin", "Admin"],
            Features =
            [
                Of("View system roles", ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("Assign or revoke system roles", ("Board", Denied), ("UsersAdmin", Allowed), ("Admin", Allowed)),
            ]
        }
    ];

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => HelpEntries;

    // Read once per process: the markdown ships inside this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> HelpEntries =
        [SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "SystemRoles", 82)];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, AuthAuditActions>();
        services.AddSingleton<INotificationSourceContributor, AuthNotificationSources>();
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");

        // Composed from every section's ISectionRoles (registered by discovery); throws on a duplicate name.
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));

        services.AddSingleton<ISystemRoleRepository, SystemRoleRepository>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // IUserDataContributor and IUserMerge reach the undecorated inner through the unkeyed concrete, which
        // forwards to this keyed registration, so there is only one inner instance.
        services.AddKeyedScoped<ISystemRoleManagement, SystemRoleService>(
            CachingSystemRoleService.InnerServiceKey);
        services.AddScoped<SystemRoleService>(sp =>
            (SystemRoleService)sp.GetRequiredKeyedService<ISystemRoleManagement>(
                CachingSystemRoleService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SystemRoleService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<SystemRoleService>());

        services.AddSingleton<CachingSystemRoleService>();
        services.AddSingleton<ISystemRoleManagement>(sp =>
            sp.GetRequiredService<CachingSystemRoleService>());
        services.AddSingleton<ISystemRoleService>(sp =>
            sp.GetRequiredService<ISystemRoleManagement>());
        services.AddSingleton<ISystemRoleWrite>(sp =>
            sp.GetRequiredService<ISystemRoleManagement>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingSystemRoleService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingSystemRoleService>());

        services.AddSingleton<IAuthorizationHandler, SystemRoleAuthorizationHandler>();

        services.AddScoped<ISignInLinkUrlBuilder, SignInLinkUrlBuilder>();
        services.AddScoped<ISignInLinkRateLimiter, SignInLinkRateLimiter>();
        services.AddScoped<ISignInLinkService, SignInLinkService>();

        services.AddScoped<AuthEmails>();
        services.AddScoped<IEmailPreviewContributor, AuthEmailPreviews>();

        services.AddSingleton<AuthMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthMetricsService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Users", [
            new("Roles", "SystemRoles", "Roles", null, null, "fa-solid fa-id-badge", UsersPolicies.Read, Weight: 10)
        ])
    ];

    internal const string RoleRenamesKey = "auth.role-renames";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(RoleRenamesKey, "Auth",
            "Rewrite system roles stored under a former role name (HumanAdmin, CampAdmin, TicketAdmin, VolunteerCoordinator, EETeamAdmin, ConsentCoordinator) to the current name",
            "RoleDefinition.FormerNames on each renamed role and the former-name reads in SystemRoleService",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Stored role", "Current role", "Valid to"],
            [.. (await sp.GetRequiredService<SystemRoleService>().GetRowsUnderFormerNamesAsync(ct))
                .Select(r => (IReadOnlyList<string>)
                [
                    r.UserId.ToString(), r.StoredName, r.CurrentName, r.ValidTo.ToIso8601() ?? "open",
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<SystemRoleService>().RewriteFormerNamesAsync(ct);

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
        [new(UserPartSlots.AdminDetail, typeof(SystemRolesCardViewComponent), Weight: 50)];
}
