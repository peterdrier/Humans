using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Services.Surveys;
using Humans.Infrastructure.Repositories.Surveys;

namespace Humans.Web.Extensions.Sections;

/// <summary>
/// DI for the Survey section. Plain Scoped service (Feedback/Issues pattern) — no caching decorator
/// per spec §12. Later phases extend this with the invite token provider (Phase 3), API key auth
/// filter + settings (Phase 6) and the GDPR contributor (Phase 7).
/// </summary>
internal static class SurveySectionExtensions
{
    internal static IServiceCollection AddSurveySection(this IServiceCollection services)
    {
        services.AddSingleton<ISurveyRepository, SurveyRepository>();   // IDbContextFactory ⇒ Singleton-safe
        services.AddScoped<SurveyService>();
        services.AddScoped<ISurveyService>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyServiceRead>(sp => sp.GetRequiredService<SurveyService>());
        return services;
    }
}
