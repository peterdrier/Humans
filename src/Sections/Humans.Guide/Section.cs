using Humans.Base.Interfaces;
using Humans.Guide.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Guide;

/// <summary>
/// <c>GuideSettings</c> and <c>IGuideContentSource</c> are registered in Shell, not here: Agent and Base read them too.
/// </summary>
public sealed class Section : ISection
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
}
