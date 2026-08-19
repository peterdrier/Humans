using Humans.Base.Interfaces;
using Humans.Email.Jobs;

namespace Humans.Email;

/// <summary>Email's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
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
