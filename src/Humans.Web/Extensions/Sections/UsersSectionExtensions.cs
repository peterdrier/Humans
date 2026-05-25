using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Users;
using Humans.Infrastructure.Services.Users;
using DashboardAdminDashboardService = Humans.Application.Services.Dashboard.AdminDashboardService;
using DashboardDashboardService = Humans.Application.Services.Dashboard.DashboardService;
using UsersUserService = Humans.Application.Services.Users.UserService;

namespace Humans.Web.Extensions.Sections;

internal static class UsersSectionExtensions
{
    internal static IServiceCollection AddUsersSection(this IServiceCollection services)
    {
        // User section — see #511 / #703. CachingUserService decorator + UserInfo read-model spans User/Profile sections.
        services.AddSingleton<IUserRepository, UserRepository>();

        // Inner Scoped + keyed; decorator resolves via IServiceScopeFactory per-call.
        services.AddKeyedScoped<IUserService, UsersUserService>(CachingUserService.InnerServiceKey);
        services.AddScoped<UsersUserService>(sp =>
            (UsersUserService)sp.GetRequiredKeyedService<IUserService>(CachingUserService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UsersUserService>());

        // Singleton so _byUserId dict survives across requests.
        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserServiceRead>(sp => sp.GetRequiredService<CachingUserService>());

        // Same Singleton instance must back invalidator + merge so external "user changed" signals hit the cache owner.
        services.AddSingleton<IUserInfoInvalidator>(sp =>
            sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserMerge>(sp =>
            sp.GetRequiredService<CachingUserService>());

        // Infrastructure-internal slice-refresh signal consumed by UserInfoSaveChangesInterceptor.
        // Not part of the cross-section §15e contract; same Singleton instance as IUserInfoInvalidator.
        services.AddSingleton<IUserInfoSliceRefresher>(sp =>
            sp.GetRequiredService<CachingUserService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingUserService>());

        // Catches Identity-machinery writes (UpdateAsync, LastLoginAt, OAuth UserEmail). Singleton — added to both DbContext + DbContextFactory pipelines.
        services.AddSingleton<UserInfoSaveChangesInterceptor>();

        // Hosted service for TrackedCache StartAsync → WarmAllAsync.
        services.AddHostedService(sp => sp.GetRequiredService<CachingUserService>());

        // Account deletion orchestrator — see #582. Reaches up to Teams/RoleAssignments/Shifts via their services so UserService/ProfileService own no outbound edges.
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();

        services.AddScoped<IDashboardService, DashboardDashboardService>();
        // Owns no tables; aggregates from section services.
        services.AddScoped<IAdminDashboardService, DashboardAdminDashboardService>();

        return services;
    }
}
