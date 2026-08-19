using Humans.GoogleIntegration.Contracts;

namespace Humans.Web.Extensions.Infrastructure;

/// <summary>
/// The one Google Workspace line that did <em>not</em> move into
/// <c>Humans.GoogleIntegration</c>'s <c>Section.Register</c> — first at that section's G5
/// (nobodies-collective/Humans#866), then again at nobodies-collective/Humans#1091 when
/// <c>Configure&lt;GoogleWorkspaceSettings&gt;</c> and its production-credentials guard
/// followed <c>GoogleWorkspaceHealthCheck</c> into the section.
/// </summary>
/// <remarks>
/// <c>GoogleWorkspaceOptions</c> is Base-owned and read from outside the section — Camps'
/// <c>CampRoleService</c> and Users' <c>ProfileController</c> both take it — so the section
/// that owns the connectors does not own this binding (Governance's rule).
/// </remarks>
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
