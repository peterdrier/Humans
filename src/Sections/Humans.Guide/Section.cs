using Humans.Application.Interfaces;
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
/// GitHub-markdown fetcher whose signatures name nothing but <c>string</c>, and three of its
/// four consumers are not Guide's — the Agent section's three preload readers, Shell's
/// <c>AgentDocsHealthCheck</c> and Base's <c>GitHubCommunityKbContentSource</c> — so it stays
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
        // Guide (in-app docs from GitHub, memory-cached, role-filtered)
        services.AddSingleton<GuideMarkdownPreprocessor>();
        services.AddSingleton<GuideHtmlPostprocessor>();
        services.AddSingleton<IGuideRenderer, GuideRenderer>();
        services.AddSingleton<IGuideContentService, GuideContentService>();
        services.AddScoped<IGuideRoleResolver, GuideRoleResolver>();
    }
}
