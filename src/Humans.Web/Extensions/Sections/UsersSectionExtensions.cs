using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Users;
using DashboardDashboardService = Humans.Application.Services.Dashboard.DashboardService;
using GovernanceMembershipCalculator = Humans.Application.Services.Governance.MembershipCalculator;
using GovernanceMembershipQuery = Humans.Application.Services.Governance.MembershipQuery;
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

        // Query adapter breaks the circular DI graph between IMembershipCalculator
        // and ITeamService / IRoleAssignmentService (both of which inject
        // ISystemTeamSync, whose implementation injects IMembershipCalculator back).
        // Only MembershipCalculator depends on the query adapter.
        services.AddScoped<IMembershipQuery, GovernanceMembershipQuery>();
        services.AddScoped<IMembershipCalculator, GovernanceMembershipCalculator>();
        services.AddScoped<IDashboardService, DashboardDashboardService>();

        return services;
    }
}
