using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Gdpr.Contracts;
using Humans.Base.Caching;
using Humans.Base.Hosting;
using Humans.Issues.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Authorization;
using Humans.Issues.Filters;
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

        // §15 repository pattern (issue #546): Singleton + IDbContextFactory (§15b) so the
        // repository owns context lifetime.
        services.AddSingleton<IIssuesRepository, IssuesRepository>();
        services.AddScoped<IssuesService>();
        services.AddScoped<IIssuesService>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssuesRetention>(sp => sp.GetRequiredService<IssuesService>());
        // Owns the user-scoped issues / issue_comments tables → GDPR export contributor
        // (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<IssuesService>());
        services.AddScoped<IIssuesBadgeCacheInvalidator, IssuesBadgeCacheInvalidator>();

        // Resource-based handler for IssuesOperationRequirement.Handle. The *policies*
        // stay in Shell's AuthorizationPolicyExtensions; only the handler moves (design §8).
        services.AddSingleton<IAuthorizationHandler, IssuesAuthorizationHandler>();

        // Issues API key. Missing/empty key is a runtime 503 at the filter,
        // not a startup failure.
        services.Configure<IssuesApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("ISSUES_API_KEY") ?? string.Empty;
        });
        services.AddScoped<IssuesApiKeyAuthFilter>();

        services.AddScoped<CleanupIssuesJob>();
    }
}
