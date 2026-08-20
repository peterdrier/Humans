using Humans.Base.Interfaces;
using Humans.Users.Jobs;

namespace Humans.Users;

/// <summary>Users' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "users-account-deletions", typeof(ProcessAccountDeletionsJob), "0 0 * * *");

        yield return new RecurringJobDescriptor(
            "users-suspend-non-compliant", typeof(SuspendNonCompliantMembersJob), "30 4 * * *");
    }
}
