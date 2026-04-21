using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Auth;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services.Auth;

namespace Humans.Web.Extensions.Sections;

internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        // Auth section (§15 migration, issue #551) — repository + Application-
        // layer service, no caching decorator. Auth writes are rare (handful of
        // admin events per month); same Scoped + DbContext repository pattern
        // as Governance.
        services.AddScoped<IRoleAssignmentRepository, RoleAssignmentRepository>();
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

        return services;
    }
}
