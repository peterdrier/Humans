using Humans.Auth.Contracts;
using Humans.Web.Authorization;

namespace Humans.Web.Extensions.Sections;

/// <summary>Auth registrations that cannot leave Shell.</summary>
internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        // An implementation of a section-owned interface that lives in Shell and cannot
        // move: HttpCurrentUserContext reads IHttpContextAccessor.
        services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

        return services;
    }
}
