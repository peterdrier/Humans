using Humans.Base.Interfaces;
using Humans.Email.Jobs;

namespace Humans.Email;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "email-outbox-process", typeof(ProcessEmailOutboxJob), "*/1 * * * *");

        yield return new RecurringJobDescriptor(
            "email-outbox-cleanup", typeof(CleanupEmailOutboxJob), "0 3 * * 0");
    }
}
