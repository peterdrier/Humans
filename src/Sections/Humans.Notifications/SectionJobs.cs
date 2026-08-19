using Humans.Base.Interfaces;
using Humans.Notifications.Jobs;

namespace Humans.Notifications;

/// <summary>Notifications' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Clean up resolved notifications older than 7 days.
        yield return new RecurringJobDescriptor(
            "notifications-cleanup", typeof(CleanupNotificationsJob), "30 4 * * *");
    }
}
