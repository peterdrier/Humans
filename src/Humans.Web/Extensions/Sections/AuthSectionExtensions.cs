using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Services.Auth;
using Humans.Auth.Contracts;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Services.Auth;
using Humans.Web.Authorization;

namespace Humans.Web.Extensions.Sections;

/// <summary>
/// Auth's Base half. The role-assignment half — repository, service, §15 decorator,
/// <see cref="IAdminAuthorizationService"/> and the resource handler — moved to
/// <c>Humans.Auth</c>'s <c>Section.Register</c> at the section's G5
/// (nobodies-collective/Humans#866); what is left here is what could not follow it.
/// </summary>
internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        // Two implementations of section-owned interfaces that live in Shell and cannot
        // move: HttpCurrentUserContext reads IHttpContextAccessor, and the claims cache
        // being invalidated belongs to Shell's RoleAssignmentClaimsTransformation.
        // Governance's rule — the section that owns the file is not always the section that
        // owns the line — read from the implementation side.
        services.AddScoped<IRoleAssignmentClaimsCacheInvalidator, RoleAssignmentClaimsCacheInvalidator>();
        services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

        // The magic-link sign-in path. MagicLinkService calls no repository and injects
        // Humans.Email.Contracts, so it is a cross-section orchestrator that a *horizontal*
        // section may not host (peters-hard-rules.md) — it stayed in Humans.Application with
        // its Infrastructure-owned token/url builder and memory-cache-backed rate limiter,
        // and AccountController stayed in Shell with it. Same split as AuditLog's
        // AuditViewerService.
        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        return services;
    }
}
