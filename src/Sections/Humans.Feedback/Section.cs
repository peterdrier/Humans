using Humans.AuditLog.Contracts;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Feedback.Data;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Feedback;

/// <summary>
/// Feedback is removed. What is left runs the migration that drops its tables and retires its notification
/// source and audit actions; the whole project goes once production has applied that migration.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, FeedbackAuditActions>();
        services.AddSingleton<INotificationSourceContributor, FeedbackNotificationSources>();
        services.AddSectionDbContext<FeedbackDbContext>(sentinelTable: "feedback_reports");
    }
}
