using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Surveys.Authorization;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Jobs;
using Humans.Surveys.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Surveys;

public sealed class Section : ISection, ISectionAdminNav, ISectionJobs
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Surveys", [
            // App access, not Board: authoring is self-service and the index is scoped to what the viewer authored,
            // so a Board gate would leave authors with a page they may use and no way to reach it.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.AppAccess),
            new("Approvals", "SurveyAdmin", "Queue", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin,
                PillCount: PillCounts.PendingApprovalQueueAsync)
        ])
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "surveys-reminder", typeof(SendSurveyReminderJob), "0 9 * * *");
    }
}

internal static class PillCounts
{
    public static async ValueTask<int?> PendingApprovalQueueAsync(IServiceProvider sp)
    {
        var surveys = sp.GetRequiredService<ISurveyService>();
        var count = (await surveys.GetPendingApprovalQueueAsync(CancellationToken.None)).Count;
        return count > 0 ? count : null;
    }
}
