using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Auth;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Repositories.Auth;
using Humans.Infrastructure.Services.Auth;
using Humans.Infrastructure.Services.Metering;

namespace Humans.Web.Extensions.Sections;

internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        // Auth section (§15 migration, issue #551) — repository + Application-
        // layer service, no caching decorator. Singleton + IDbContextFactory
        // pattern (§15b) so the repository owns context lifetime.
        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator, RoleAssignmentClaimsCacheInvalidator>();

        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());

        // Auth section (§15 migration, issue #551) — Application-layer
        // MagicLinkService + Infrastructure-owned token/url builder and
        // memory-cache-backed rate limiter (same pattern as
        // CommunicationPreferenceService + UnsubscribeTokenProvider).
        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        // Metrics: Auth owns humans.role_assignments_active.
        services.AddHostedService<AuthMetricsRegistrar>();

        return services;
    }
}
