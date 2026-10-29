using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Authorization;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using Humans.Issues.Jobs;
using Humans.Issues.Services;
using Humans.Issues.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Issues;

/// <summary>
/// Issue reads, including per-viewer actionable counts, query the repository directly.
/// </summary>
public sealed class Section : ISection, ISectionAdminNav, ISectionChrome, ISectionJobs
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, IssuesAuditActions>();
        services.AddSectionDbContext<IssuesDbContext>(sentinelTable: "issues");

        services.AddSingleton<IIssuesRepository, IssuesRepository>();

        services.AddSingleton<IssueSectionRouting>();
        services.AddScoped<IssuesEmails>();
        services.AddScoped<IEmailPreviewContributor, IssuesEmailPreviews>();

        services.AddScoped<IssuesService>();
        services.AddScoped<IIssuesService>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssuesRetention>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssueTriage>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<IssuesService>());

        services.AddSingleton<IAuthorizationHandler, IssuesAuthorizationHandler>();

        services.AddScoped<CleanupIssuesJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Issues", [
            new("Issues", "Issues", "Index", null, null, "fa-solid fa-bug", PolicyNames.AnyAdminRole, Weight: 0)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.UserMenu, typeof(IssuesUserMenuViewComponent))];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "issues-cleanup", typeof(CleanupIssuesJob), "0 5 * * *");
    }
}
