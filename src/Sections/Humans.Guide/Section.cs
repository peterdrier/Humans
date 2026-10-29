using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Guide.Health;
using Humans.Guide.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Guide;

/// <summary>
/// <c>GuideSettings</c> and <c>IGuideContentSource</c> are registered in Shell, not here: Agent and Base read them too.
/// </summary>
public sealed class Section :
    ISection,
    ISectionAnnotations,
    ISectionConfiguration,
    ISectionHealthChecks,
    ISectionNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<GuideHtmlPostprocessor>();
        services.AddSingleton<IGuideRenderer, GuideRenderer>();
        services.AddSingleton<GuideRolePrivilegeMap>();
        services.AddSingleton<GuideSegmenter>();
        services.AddSingleton<IGuideContentService, GuideContentService>();
        services.AddScoped<IGuideRoleResolver, GuideRoleResolver>();
    }

    IEnumerable<SectionAnnotation> ISectionAnnotations.Annotations() =>
        GuideFiles.Sections.Select(stem => new SectionAnnotation(stem, "Guide page", $"/Guide/{stem}"));

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Guide:Owner", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:Repository", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:Branch", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:FolderPath", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:CacheTtlHours", "Guide");
        configuration.GetOptionalSetting(registry, "Guide:AccessToken", "Guide", isSensitive: true);
    }

    void ISectionHealthChecks.AddHealthChecks(IHealthChecksBuilder builder, IConfiguration configuration)
    {
        builder.AddCheck<GitHubHealthCheck>("github", tags: [HealthCheckTags.External]);
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
    [
        new(
            "Guide",
            Controller: "Guide",
            Action: "Index",
            Weight: 1010)
    ];
}
