using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Services.Surveys;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Surveys;
using Humans.Infrastructure.Services.Surveys;
using Humans.Web.Filters;

namespace Humans.Web.Extensions.Sections;

/// <summary>
/// DI for the Survey section. Plain Scoped service (Feedback/Issues pattern) — no caching decorator
/// per spec §12. Includes the invite token provider (Phase 3) and the key-authed analysis API
/// filter + settings (Phase 6), and the GDPR export contributor (Phase 7).
/// </summary>
internal static class SurveySectionExtensions
{
    internal static IServiceCollection AddSurveySection(this IServiceCollection services)
    {
        services.AddSingleton<ISurveyRepository, SurveyRepository>();   // IDbContextFactory ⇒ Singleton-safe
        services.AddScoped<SurveyService>();
        services.AddScoped<ISurveyService>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyServiceRead>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SurveyService>());   // Phase 7: GDPR export contributor
        services.AddScoped<ISurveyInviteTokenProvider, SurveyInviteTokenProvider>();
        services.AddScoped<SendSurveyReminderJob>();   // recurring 7-day reminder (RecurringJobExtensions)

        // Survey analysis API key. Missing/empty key is a runtime 503 at the filter, not a startup failure.
        services.Configure<SurveyApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("SURVEY_API_KEY") ?? string.Empty;
        });
        services.AddScoped<SurveyApiKeyAuthFilter>();

        return services;
    }
}
