using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class GuideSectionExtensions
{
    internal static IServiceCollection AddGuideSection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GuideSettings>(configuration.GetSection(GuideSettings.SectionName));

        // Guide (in-app docs from GitHub, memory-cached, role-filtered)
        services.AddSingleton<GuideMarkdownPreprocessor>();
        services.AddSingleton<GuideHtmlPostprocessor>();
        services.AddSingleton<IGuideRenderer, GuideRenderer>();
        services.AddSingleton<IGuideContentSource, GitHubGuideContentSource>();
        services.AddSingleton<IGuideContentService, GuideContentService>();
        services.AddScoped<IGuideRoleResolver, GuideRoleResolver>();

        return services;
    }
}
