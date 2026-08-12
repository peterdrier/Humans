using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Filters;
using Humans.Surveys.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Surveys;

/// <summary>
/// Surveys' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. Plain Scoped service (Feedback/Issues
/// pattern) — no caching decorator, per the section design spec §12.
/// </summary>
/// <remarks>
/// <c>SendSurveyReminderJob</c> is <em>not</em> registered here: recurring jobs are named by
/// concrete type in Shell's <c>UseHumansRecurringJobs</c> roll-call and there is no discovery
/// seam for them yet, so it stays in <c>Humans.Infrastructure/Jobs</c> and reaches the section
/// through <see cref="ISurveyReminderSender"/> (design §15.6b).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<SurveysDbContext>(sentinelTable: "surveys");

        services.AddSingleton<ISurveyRepository, SurveyRepository>();   // IDbContextFactory ⇒ Singleton-safe
        services.AddScoped<SurveyService>();
        services.AddScoped<ISurveyService>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyReminderSender>(sp => sp.GetRequiredService<SurveyService>());
        // Owns the user-scoped survey_responses/survey_invitations tables → GDPR export
        // contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyInviteTokenProvider, SurveyInviteTokenProvider>();

        // Survey analysis API key. Missing/empty key is a runtime 503 at the filter, not a startup failure.
        services.Configure<SurveyApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("SURVEY_API_KEY") ?? string.Empty;
        });
        services.AddScoped<SurveyApiKeyAuthFilter>();
    }
}
