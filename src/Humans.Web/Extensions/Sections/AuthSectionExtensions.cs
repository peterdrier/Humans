using Humans.Base.Interfaces.Caching;
using Humans.Auth.Contracts;
using Humans.Base.Caching;
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
        // An implementation of a section-owned interface that lives in Shell and cannot
        // move: HttpCurrentUserContext reads IHttpContextAccessor.
        // Governance's rule — the section that owns the file is not always the section that
        // owns the line — read from the implementation side.
        services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

        // The magic-link sign-in path (IMagicLinkService + IMagicLinkUrlBuilder +
        // IMagicLinkRateLimiter) left this file for Humans.Auth's Section.Register at
        // nobodies-collective/Humans#866 G5 lane 4b-2i. AccountController stayed here.

        return services;
    }
}
