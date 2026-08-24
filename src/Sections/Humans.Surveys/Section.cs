using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Jobs;
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
/// <c>SendSurveyReminderJob</c> moved into <c>Contracts/</c> at G5 lane 5b-5
/// (nobodies-collective/Humans#866) and then into <c>Jobs/</c> with the HUM0034 carve-out
/// (nobodies-collective/Humans#1353), and drives <see cref="ISurveyReminderSender"/>. Its
/// registration and schedule are contributed via <c>SectionJobs.cs</c> (#1074's jobs seam).
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
        services.AddScoped<ISurveyAnalysisRead>(sp => sp.GetRequiredService<SurveyService>());
        // Owns the user-scoped survey_responses/survey_invitations tables → GDPR export
        // contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyInviteTokenProvider, SurveyInviteTokenProvider>();
        services.AddScoped<SurveyPreviewTokenProvider>();
        services.AddScoped<ISurveyPreviewEmailService, SurveyPreviewEmailService>();

        services.AddScoped<SendSurveyReminderJob>();
    }
}
