using Humans.Base.Interfaces;
using Humans.Issues.Jobs;

namespace Humans.Issues;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "issues-cleanup", typeof(CleanupIssuesJob), "0 5 * * *");
    }
}
