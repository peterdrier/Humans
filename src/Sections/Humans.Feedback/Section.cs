using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Feedback.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Feedback;

/// <summary>No caching decorator: the service caches the nav-badge count itself.</summary>
public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionJobs
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Issues", [
            new("Feedback queue", "Feedback", "Index", null, null, "fa-solid fa-comment-dots", PolicyNames.AdminOnly, Weight: 10,
                 PillCount: PillCounts.FeedbackQueueAsync)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("feedback.open", "Open feedback", "fa-solid fa-comment-dots", OpenAsync,
            Policy: PolicyNames.AdminOnly, Weight: 50)
    ];

    private static async ValueTask<AdminTileValue?> OpenAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = await sp.GetRequiredService<FeedbackService>().GetActionableCountAsync(ct);
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture), Summary: $"{count} open feedback");
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("feedback-reply-emails", typeof(FeedbackReplyDelivery), "* * * * *");
    }
}

internal static class PillCounts
{
    public static async ValueTask<int?> FeedbackQueueAsync(IServiceProvider sp)
    {
        var feedback = sp.GetRequiredService<FeedbackService>();
        var count = await feedback.GetActionableCountAsync(CancellationToken.None);
        return count > 0 ? count : null;
    }
}
