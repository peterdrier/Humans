using Humans.Base.Interfaces;
using Humans.Notifications.Jobs;

namespace Humans.Notifications;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "notifications-cleanup", typeof(CleanupNotificationsJob), "30 4 * * *");
    }
}
