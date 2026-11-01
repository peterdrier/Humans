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
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.AuditLog.Contracts;

namespace Humans.Feedback;

/// <summary>No caching decorator: the service caches the nav-badge count itself.</summary>
public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionJobs,
    ISectionDataMigrations
{
    private const string ScreenshotsPrefix = "uploads/feedback";

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(FeedbackRoles.FeedbackAdmin, "Feedback Admin",
            "Listeners of the community voice. They fielded bug reports, feature requests, and questions back when Feedback was how you reached us; these days that lives in Issues.",
            "fa-solid fa-comments"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, FeedbackAuditActions>();
        services.AddSingleton<INotificationSourceContributor, FeedbackNotificationSources>();
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

    // Feedback is being removed and its tables dropped; those rows are the only index to these files, so the
    // whole prefix goes first: a reporter's screenshots must not outlive the report that pointed to them.
    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new("feedback.delete-screenshots", "Feedback",
            "Delete every Feedback screenshot before the section and its tables are removed",
            "The Feedback section and its tables",
            ScreenshotsStatusAsync, DeleteScreenshotsAsync),
    ];

    private static Task<DataMigrationStatus> ScreenshotsStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        Task.FromResult(new DataMigrationStatus(["File"],
            [.. ScreenshotFiles(sp).Select(f => (IReadOnlyList<string>)[f])]));

    private static async Task<int> DeleteScreenshotsAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct)
    {
        var count = ScreenshotFiles(sp).Count;
        await sp.GetRequiredService<IFileStorage>().DeleteAsync(ScreenshotsPrefix, ct, recursiveDirectory: true);
        return count;
    }

    // Lists the directory, not the rows, so orphaned files go too. IFileStorage has no listing, yet the operator
    // must see each pending file before confirming; the root matches FileSystemFileStorage's.
    private static List<string> ScreenshotFiles(IServiceProvider sp)
    {
        var root = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "wwwroot");
        var dir = Path.Combine(root, ScreenshotsPrefix.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)]
            : [];
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
