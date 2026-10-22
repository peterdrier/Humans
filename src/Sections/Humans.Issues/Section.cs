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
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Issues;

/// <summary>
/// No caching decorator: the service owns the per-viewer badge count cache and evicts it on every count-shifting
/// write.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
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
