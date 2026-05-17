using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Auth;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Services.Auth;
using Humans.Web.Authorization;

namespace Humans.Web.Extensions.Sections;

internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        // Auth section (§15 migration, issue #551) — repository + Application-
        // layer service. Singleton + IDbContextFactory pattern (§15b) so the
        // repository owns context lifetime. CachingRoleAssignmentService
        // (issue #749) caches the row set so cross-section reads such as
        // GetActiveCountsByRoleAsync derive from RAM. Invalidation is
        // service-level: RoleAssignmentService's writes call
        // IRoleAssignmentCacheInvalidator.InvalidateAll() directly. Single
        // writer (this service) = no EF interceptor needed.
        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator, RoleAssignmentClaimsCacheInvalidator>();
        services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // Issue #749: Inner RoleAssignmentService registered keyed under
        // CachingRoleAssignmentService.InnerServiceKey; the unkeyed concrete
        // forwards to the keyed registration via cast so IUserDataContributor
        // and IUserMerge resolve the same scoped instance the decorator wraps.
        // Mirrors the ConsentService pattern in LegalAndConsentSectionExtensions.
        services.AddKeyedScoped<IRoleAssignmentService, RoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey);
        services.AddScoped<RoleAssignmentService>(sp =>
            (RoleAssignmentService)sp.GetRequiredKeyedService<IRoleAssignmentService>(
                CachingRoleAssignmentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddSingleton<CachingRoleAssignmentService>();
        services.AddSingleton<IRoleAssignmentService>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<IRoleAssignmentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleAssignmentService>());

        // Auth section (§15 migration, issue #551) — Application-layer
        // MagicLinkService + Infrastructure-owned token/url builder and
        // memory-cache-backed rate limiter (same pattern as
        // CommunicationPreferenceService + UnsubscribeTokenProvider).
        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        return services;
    }
}
