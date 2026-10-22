using Humans.GoogleIntegration.Contracts;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// <c>GoogleWorkspaceOptions</c> is Base-owned and read outside Humans.GoogleIntegration (Camps' CampRoleService,
/// Users' ProfileEmailsController), so its binding stays in Shell.
/// </summary>
internal static class GoogleWorkspaceInfrastructureExtensions
{
    internal static IServiceCollection AddGoogleWorkspaceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GoogleWorkspaceOptions>(configuration.GetSection(GoogleWorkspaceOptions.SectionName));

        return services;
    }
}
