using Humans.Audit.Contracts;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Roles.Data;
using Humans.Roles.Services;
using Humans.Roles.ViewComponents;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Roles;

public sealed class Section : ISection, ISectionRoles, ISectionNav, ISectionAdminNav, ISectionPolicies, ITeamRolesPart
{
    Type ITeamRolesPart.Card => typeof(TeamRolesCardViewComponent);

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(RolesSystemRoles.RolesAdmin, "Roles Admin",
            "Sets up each team's coordinator role and who holds it, and can manage any team's roles. Holds no permissions itself.",
            "fa-solid fa-user-tag"),
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Roles", Controller: "Roles", Action: "Index", Weight: 21)];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [new("Roles", [new("Roles", "Roles", "Admin", null, null, "fa-solid fa-user-tag", RolesPolicies.Read)])];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(RolesPolicies.Read, policy => policy.RequireRole(RolesSystemRoles.RolesAdmin, RoleNames.Board, RoleNames.Admin));

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, RolesAuditActions>();
        services.AddSectionDbContext<RolesDbContext>(sentinelTable: "role_definitions");

        services.AddSingleton<IRoleRepository, RoleRepository>();

        services.AddKeyedScoped<IRoleManagementService, RoleService>(CachingRoleService.InnerServiceKey);
        services.AddSingleton<CachingRoleService>();
        services.AddSingleton<IRoleManagementService>(sp => sp.GetRequiredService<CachingRoleService>());
        services.AddSingleton<IRoleService>(sp => sp.GetRequiredService<CachingRoleService>());
        services.AddSingleton<IRoleWrite>(sp => sp.GetRequiredService<CachingRoleService>());
        services.AddSingleton<IPermissionSource>(sp => sp.GetRequiredService<CachingRoleService>());

        // Erasure and the merge change cached rows, so the fan-outs bind to the decorator.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingRoleService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingRoleService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingRoleService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleService>());
    }
}
