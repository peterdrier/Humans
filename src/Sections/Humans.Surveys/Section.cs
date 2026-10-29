using Humans.Users.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Surveys.Authorization;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Jobs;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Surveys;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, SurveysAuditActions>();
        services.AddSectionDbContext<SurveysDbContext>(sentinelTable: "surveys");

        services.AddSingleton<ISurveyRepository, SurveyRepository>();   // IDbContextFactory ⇒ Singleton-safe
        services.AddScoped<SurveyService>();
        services.AddScoped<ISurveyService>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyReminderSender, SurveyReminderSender>();
        services.AddScoped<ISurveyAnalysisRead>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<SurveyService>());
        services.AddScoped<ISurveyInviteTokenProvider, SurveyInviteTokenProvider>();
        services.AddScoped<SurveyPreviewTokenProvider>();
        services.AddScoped<ISurveyPreviewEmailService, SurveyPreviewEmailService>();

        services.AddScoped<SendSurveyReminderJob>();

        services.AddScoped<SurveysEmails>();
        services.AddScoped<IEmailPreviewContributor, SurveysEmailPreviews>();

        services.AddSingleton<IAuthorizationHandler, SurveyAuthorizationHandler>();
    }
}
