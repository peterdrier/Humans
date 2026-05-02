using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Issues;
using Humans.Web.Filters;
using IssuesApplicationService = Humans.Application.Services.Issues.IssuesService;

namespace Humans.Web.Extensions.Sections;

internal static class IssuesSectionExtensions
{
    internal static IServiceCollection AddIssuesSection(this IServiceCollection services)
    {
        // Issues section — §15 repository + Application-layer service, no caching decorator.
        // Singleton + IDbContextFactory pattern (§15b) so the repository owns context lifetime.
        services.AddSingleton<IIssuesRepository, IssuesRepository>();
        services.AddScoped<IssuesApplicationService>();
        services.AddScoped<IIssuesService>(sp => sp.GetRequiredService<IssuesApplicationService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<IssuesApplicationService>());
        services.AddScoped<IIssuesBadgeCacheInvalidator, IssuesBadgeCacheInvalidator>();

        // Issues API key. Missing/empty key is a runtime 503 at the filter,
        // not a startup failure.
        services.Configure<IssuesApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("ISSUES_API_KEY") ?? string.Empty;
        });
        services.AddScoped<IssuesApiKeyAuthFilter>();

        // Daily retention job — purges terminal issues older than 6 months.
        services.AddScoped<CleanupIssuesJob>();

        return services;
    }
}
