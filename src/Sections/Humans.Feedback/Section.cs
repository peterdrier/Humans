using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Email.Contracts;
using Humans.Feedback.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Services;
using Humans.Base.Hosting;
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

        // §15b (nobodies-collective/Humans#546): Singleton + IDbContextFactory so the
        // repository owns context lifetime.
        services.AddSingleton<IFeedbackRepository, FeedbackRepository>();
        // Feedback owns its email copy and its gallery samples; Email keeps the mechanics
        // (memory/architecture/email-templates-live-in-sender.md).
        services.AddScoped<FeedbackEmails>();
        services.AddScoped<IEmailPreviewContributor, FeedbackEmailPreviews>();

        services.AddScoped<FeedbackService>();
        services.AddScoped<IFeedbackServiceRead>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IFeedbackTriage>(sp => sp.GetRequiredService<FeedbackService>());
        // Owns the user-scoped feedback_reports / feedback_messages tables → GDPR export
        // contributor and account-merge fold participant (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<FeedbackService>());
    }
}
