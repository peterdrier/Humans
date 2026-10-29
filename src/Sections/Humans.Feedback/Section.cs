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
using Humans.AuditLog.Contracts;

namespace Humans.Feedback;

/// <summary>No caching decorator: the service caches the nav-badge count itself.</summary>
public sealed class Section : ISection, ISectionRoles
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(FeedbackRoles.FeedbackAdmin, "Feedback Admin",
            "Listeners of the community voice. They fielded bug reports, feature requests, and questions back when Feedback was how you reached us; these days that lives in Issues.",
            "fa-solid fa-comments"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, FeedbackAuditActions>();
        services.AddSectionDbContext<FeedbackDbContext>(sentinelTable: "feedback_reports");

        services.AddSingleton<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<FeedbackEmails>();
        services.AddScoped<IEmailPreviewContributor, FeedbackEmailPreviews>();

        services.AddScoped<FeedbackReplyDelivery>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<IFeedbackService>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IFeedbackTriage>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<FeedbackService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<FeedbackService>());
    }
}
