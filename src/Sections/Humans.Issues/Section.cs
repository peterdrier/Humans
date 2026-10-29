using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Email.Contracts;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using Humans.Issues.Authorization;
using Humans.Issues.Jobs;
using Humans.Issues.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Issues;

/// <summary>
/// Issue reads, including per-viewer actionable counts, query the repository directly.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, IssuesNotificationSources>();
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
}
