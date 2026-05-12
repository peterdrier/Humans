using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Infrastructure.Repositories.Users;
using DashboardAdminDashboardService = Humans.Application.Services.Dashboard.AdminDashboardService;
using DashboardDashboardService = Humans.Application.Services.Dashboard.DashboardService;
using UsersUserService = Humans.Application.Services.Users.UserService;

namespace Humans.Web.Extensions.Sections;

internal static class UsersSectionExtensions
{
    internal static IServiceCollection AddUsersSection(this IServiceCollection services)
    {
        // User section — §15 repository pattern (issue #511).
        // No decorator / cache: User is ~500 rows with no stitched projection or
        // hot bulk-read path; see docs/superpowers/specs/2026-04-21-issue-511-user-migration.md
        // for the Option A rationale. IUserRepository is Singleton
        // (IDbContextFactory-based) so the service can inject it directly.
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddScoped<UsersUserService>();
        services.AddScoped<IUserService>(sp => sp.GetRequiredService<UsersUserService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UsersUserService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<UsersUserService>());

        // Account deletion orchestrator (issue nobodies-collective/Humans#582). Single entry point for
        // user-requested / admin-initiated / expiry-triggered deletion paths.
        // Lives alongside UserService because the User aggregate is the
        // deletion anchor; reaches up to Teams / RoleAssignments / Shifts
        // via their service interfaces so UserService/ProfileService retain
        // no outbound edges to higher-level sections.
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();

        services.AddScoped<IDashboardService, DashboardDashboardService>();
        // Admin dashboard aggregator — owns no tables; aggregates user
        // partition, application stats, and language distribution from
        // the relevant section services.
        services.AddScoped<IAdminDashboardService, DashboardAdminDashboardService>();

        return services;
    }
}
