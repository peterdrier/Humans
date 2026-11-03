using Humans.Base.Authorization;
using Humans.Base.Constants;
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

public sealed class Section : ISection, ISectionRoles, ISectionAdminNav, ISectionPolicies, ISectionJobs
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(SurveysRoles.SurveysAdmin, "Surveys Admin",
            "Survey stewards. They approve or reject submitted surveys, edit any survey, open and close them, send the invitations, and read every survey's results.",
            "fa-solid fa-square-poll-vertical"),
    ];

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

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(SurveysPolicies.Read, policy =>
            policy.RequireRole(SurveysRoles.SurveysAdmin, RoleNames.Board, RoleNames.Admin));
        options.AddPolicy(SurveysPolicies.Manage, policy =>
            policy.RequireRole(SurveysRoles.SurveysAdmin, RoleNames.Admin));
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Surveys", [
            // App access, not Board: authoring is self-service and the index is scoped to what the viewer authored,
            // so a Board gate would leave authors with a page they may use and no way to reach it.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.AppAccess),
            new("Approvals", "SurveyAdmin", "Queue", null, null, "fa-solid fa-square-poll-vertical", SurveysPolicies.Read,
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
