using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Feedback.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Filters;
using Humans.Feedback.Services;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Feedback;

/// <summary>
/// Feedback's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. Admin-review-only and low-traffic, so
/// there is no caching decorator; the service caches the nav-badge count itself.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<FeedbackDbContext>(sentinelTable: "feedback_reports");

        // §15 repository pattern (issue #546): Singleton + IDbContextFactory (§15b) so the
        // repository owns context lifetime.
        services.AddSingleton<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<IFeedbackServiceRead>(sp => sp.GetRequiredService<FeedbackService>());
        // Owns the user-scoped feedback_reports / feedback_messages tables → GDPR export
        // contributor and account-merge fold participant (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<FeedbackService>());

        // Feedback API key
        services.Configure<FeedbackApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY") ?? string.Empty;
        });
        services.AddScoped<FeedbackApiKeyAuthFilter>();
    }
}
