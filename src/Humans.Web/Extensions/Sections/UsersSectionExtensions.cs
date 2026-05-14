using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.HostedServices;
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
        // User section — §15 repository pattern (issue #511).
        // Issue #703: added CachingUserService decorator + UserInfo cached
        // read-model spanning the User and Profile sections. The base
        // UserService is now registered keyed under "user-inner"; unkeyed
        // IUserService resolves to the Singleton decorator. UserService still
        // owns Application-side write paths and invalidates IFullProfileInvalidator
        // for FullProfile-visible field changes — the SaveChanges interceptor
        // (UserInfoSaveChangesInterceptor) handles UserInfo-cache invalidation
        // for every persisted mutation including Identity-machinery writes.
        services.AddSingleton<IUserRepository, UserRepository>();

        // Inner UserService — Scoped + keyed. CachingUserService resolves via
        // IServiceScopeFactory per-call.
        services.AddKeyedScoped<IUserService, UsersUserService>(CachingUserService.InnerServiceKey);
        services.AddScoped<UsersUserService>(sp =>
            (UsersUserService)sp.GetRequiredKeyedService<IUserService>(CachingUserService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<UsersUserService>());

        // CachingUserService — Singleton so the _byUserId dict persists across
        // requests. Resolves IUserRepository / IUserEmailRepository /
        // IProfileRepository / IContactFieldRepository directly (all Singleton
        // IDbContextFactory-based); resolves the Scoped inner IUserService via
        // IServiceScopeFactory per-call.
        services.AddSingleton<CachingUserService>();
        services.AddSingleton<IUserService>(sp => sp.GetRequiredService<CachingUserService>());

        // IUserInfoInvalidator and IUserMerge must resolve to the SAME Singleton
        // CachingUserService instance that backs IUserService — same critical
        // aliasing rule as IFullProfileInvalidator → CachingProfileService.
        services.AddSingleton<IUserInfoInvalidator>(sp =>
            sp.GetRequiredService<CachingUserService>());
        services.AddSingleton<IUserMerge>(sp =>
            sp.GetRequiredService<CachingUserService>());

        // Surface UserInfo cache diagnostics on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingUserService>());

        // SaveChanges interceptor — catches Identity-machinery writes
        // (UserManager.UpdateAsync, sign-in LastLoginAt, OAuth UserEmail
        // creation) and every other persisted mutation to the 8 contributing
        // tables. Registered as Singleton so the same instance is added to
        // both AddDbContext and AddDbContextFactory option pipelines.
        services.AddSingleton<UserInfoSaveChangesInterceptor>();

        // Eagerly warm the UserInfo dict at startup. Failures are logged and
        // swallowed; lazy population still works.
        services.AddHostedService<UserInfoWarmupHostedService>();

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
