using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class AuthSectionExtensions
{
    internal static IServiceCollection AddAuthSection(this IServiceCollection services)
    {
        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddScoped<IMagicLinkService, MagicLinkService>();

        return services;
    }
}
