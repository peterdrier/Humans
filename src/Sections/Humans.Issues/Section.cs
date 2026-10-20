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
/// Issues' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. No caching decorator: the service
/// owns the per-viewer nav-badge count cache itself and evicts it on every count-shifting
/// write (memory/code/viewcomponent-no-cache.md).
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<IssuesDbContext>(sentinelTable: "issues");

        // Singleton + IDbContextFactory so the repository owns context lifetime.
        services.AddSingleton<IIssuesRepository, IssuesRepository>();

        // The queue routing table, assembled from what every section declared through
        // IIssueQueueOwner — Issues names no section.
        services.AddSingleton<IssueSectionRouting>();
        // Issues owns its email copy and its gallery samples; Email keeps the mechanics
        // (memory/architecture/email-templates-live-in-sender.md).
        services.AddScoped<IssuesEmails>();
        services.AddScoped<IEmailPreviewContributor, IssuesEmailPreviews>();

        services.AddScoped<IssuesService>();
        services.AddScoped<IIssuesService>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssuesRetention>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssueTriage>(sp => sp.GetRequiredService<IssuesService>());
        // Owns the user-scoped issues / issue_comments tables → GDPR export contributor.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<IssuesService>());

        // Resource-based handler for IssuesOperationRequirement.Handle. The *policies*
        // register through ISectionPolicies when the section owns them; shared platform policies stay in Shell.
        services.AddSingleton<IAuthorizationHandler, IssuesAuthorizationHandler>();

        services.AddScoped<CleanupIssuesJob>();
    }
}
