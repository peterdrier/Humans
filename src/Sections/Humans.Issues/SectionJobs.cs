using Humans.Base.Interfaces;
using Humans.Issues.Jobs;

namespace Humans.Issues;

/// <summary>Issues' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "issues-cleanup", typeof(CleanupIssuesJob), "0 5 * * *");
    }
}
