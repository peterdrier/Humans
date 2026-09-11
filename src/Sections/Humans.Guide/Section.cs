using Humans.Base.Interfaces;
using Humans.Guide.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Guide;

/// <summary>
/// Guide's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// Two of <c>AddGuideSection</c>'s registrations did not come along and now sit in Shell's
/// <c>InfrastructureServiceCollectionExtensions</c>: <c>Configure&lt;GuideSettings&gt;</c> and
/// <c>IGuideContentSource → GitHubGuideContentSource</c>. The interface is a plain
/// GitHub-markdown fetcher whose signatures name nothing but <c>string</c>, and its
/// consumers are not Guide's — the Agent section's <c>AgentSectionDocReader</c>,
/// <c>AgentFeatureSpecReader</c>, <c>CommunityFaqReader</c> and <c>AgentDocsHealthCheck</c>,
/// and Base's <c>GitHubCommunityKbContentSource</c> — so it stays
/// in Base with the settings type it binds (design §15 step 5b's connector test; the section
/// that owns the file is not always the section that owns the line).
/// <para>
/// Guide owns no tables, so there is no <c>AddSectionDbContext</c> line and no repository.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<GuideHtmlPostprocessor>();
        services.AddSingleton<IGuideRenderer, GuideRenderer>();
        services.AddSingleton<IGuideContentService, GuideContentService>();
        services.AddScoped<IGuideRoleResolver, GuideRoleResolver>();
    }
}
